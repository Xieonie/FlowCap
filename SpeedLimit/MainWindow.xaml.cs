using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpeedLimit;

/// <summary>Zeile der Verbrauchsansicht.</summary>
public sealed record UsageRow(string Name, ImageSource? Icon, string DownText, string UpText, string TotalText, double DownShare, double TotalShare);

/// <summary>Zeile der Verbindungsliste.</summary>
public sealed record ConnRow(string Host, string Detail);

public partial class MainWindow : Window
{
    readonly Throttler throttler;
    readonly Settings settings;
    readonly UsageStore usage;
    readonly List<AppRow> rows = [];
    readonly Dictionary<string, AppRow> byName = [];
    // Sortieren/Filtern machen wir selbst per Insert/Move/Remove statt per Live-Shaping der CollectionView:
    // Live-Shaping an/aus schalten führte zu "ItemsControl is inconsistent with its items source"
    readonly ObservableCollection<AppRow> visible = [];
    readonly ObservableCollection<ConnRow> connRows = [];
    readonly ConcurrentDictionary<IPAddress, string> hostNames = new();
    readonly Dictionary<string, ImageSource?> icons = []; // auch für Programme, die nicht mehr laufen (Verbrauchsansicht)
    readonly AppRow totalRow, gameRow;
    // shortcut: nur pro Programmlauf gemerkt – nach einem Neustart kommt eine Datenlimit-Meldung im selben Zeitraum ggf. noch einmal
    readonly HashSet<string> notified = [];
    DateTime seedUntil = DateTime.MinValue;
    string? gameModeApp;
    ScheduleRule? currentRule, suppressedRule; // suppressed = Regel, die der Nutzer per manueller Profilwahl übersteuert hat
    bool showingProfile;

    /// <summary>Profilnamen für Kopfzeile, Einstellungen und Zeitplan-Auswahl.</summary>
    public ObservableCollection<string> ProfileNames { get; } = [];

    /// <summary>Auswahl im Detailbereich: "Kein Proxy" plus alle eingerichteten Proxys.</summary>
    public ObservableCollection<string> ProxyChoices { get; } = [AppRow.NoProxy];

    /// <summary>Auswahl im Detailbereich: "Kein VPN" plus alle geladenen WireGuard-Configs.</summary>
    public ObservableCollection<string> VpnChoices { get; } = [AppRow.NoVpn];

    // Laufende Tunnel (nur solange ein Programm zugewiesen ist) und ihr letzter Zustand für den Kill-Switch
    readonly Dictionary<string, WireGuardTunnel> tunnels = [];
    readonly Dictionary<string, TunnelState> lastTunnelState = [];
    readonly Stopwatch sinceTick = Stopwatch.StartNew();
    // Schieberegler feuern beim Ziehen viele Änderungen – Datei erst schreiben, wenn kurz Ruhe ist
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    double[] totalDownHistory = new double[AppRow.HistoryLength], totalUpHistory = new double[AppRow.HistoryLength];
    int ticks;

    // Nur das Layout laden – vom Ladetest genutzt, der ohne Treiber/Adminrechte läuft
    MainWindow()
    {
        InitializeComponent();
        throttler = null!;
        settings = null!;
        usage = null!;
        totalRow = gameRow = null!;
    }

    /// <summary>Meldung für das Tray-Symbol: Titel, Text, optional das betroffene Programm (Klick wählt es aus).</summary>
    public event Action<string, string, string?>? Notice;

    public MainWindow(Throttler throttler, Settings settings, UsageStore usage) : this()
    {
        this.throttler = throttler;
        this.settings = settings;
        this.usage = usage;
        AppList.ItemsSource = visible;
        ConnList.ItemsSource = connRows;
        saveTimer.Tick += (_, _) => SaveNow();

        AppRow.UseBits = settings.UseBits;
        (settings.UseBits ? UnitBits : UnitBytes).IsChecked = true;
        (settings.GraphSeconds switch { 300 => Range300, 900 => Range900, _ => Range60 }).IsChecked = true;
        AutostartBox.IsChecked = Autostart.IsEnabled;
        StartInTrayBox.IsChecked = settings.StartInTray;
        totalRow = new AppRow("Gesamtlimit", null, settings.TotalLimit with { Blocked = false }, OnTotalLimitChanged);
        TotalEditor.Content = totalRow;
        gameRow = new AppRow("Andere Programme", null, settings.GameModeLimit with { Blocked = false }, OnGameLimitChanged);
        GameEditor.Content = gameRow;
        GameModeBox.IsChecked = settings.GameModeEnabled;
        NotifyNewAppsBox.IsChecked = settings.NotifyNewApps;
        UpdatePriorityText();

        settings.EnsureProfiles();
        foreach (var name in settings.Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase)) ProfileNames.Add(name);
        ProfileBox.ItemsSource = ProfileNames;
        ProfileList.ItemsSource = ProfileNames;
        ShowActiveProfile();
        ScheduleList.ItemsSource = settings.Schedules;
        foreach (var rule in settings.Schedules) rule.PropertyChanged += Rule_PropertyChanged;

        foreach (var p in settings.Proxies) ProxyChoices.Add(p.Name);
        ProxyList.ItemsSource = settings.Proxies;
        void ShowProxyEmpty() => ProxyEmpty.Visibility = settings.Proxies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        settings.Proxies.CollectionChanged += (_, _) => ShowProxyEmpty();
        ShowProxyEmpty();

        foreach (var v in settings.Vpns) VpnChoices.Add(v.Name);
        VpnList.ItemsSource = settings.Vpns;
        foreach (var v in settings.Vpns) v.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VpnEntry.KillSwitch)) ScheduleSave(); };
        void ShowVpnEmpty() => VpnEmpty.Visibility = settings.Vpns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        settings.Vpns.CollectionChanged += (_, _) => ShowVpnEmpty();
        ShowVpnEmpty();

        SpeedHistory.ItemsSource = settings.SpeedTests;
        void ShowHistoryEmpty() => SpeedHistoryEmpty.Visibility = settings.SpeedTests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        settings.SpeedTests.CollectionChanged += (_, _) => ShowHistoryEmpty();
        ShowHistoryEmpty();
    }

    /// <summary>Messung starten – erst nachdem das Tray auf <see cref="Notice"/> hört, sonst gingen Meldungen beim Start verloren.</summary>
    public void Start()
    {
        ApplyProxies();
        ApplyVpns();
        Tick();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Tick()
    {
        var secs = Math.Max(sinceTick.Elapsed.TotalSeconds, 0.001);
        sinceTick.Restart();
        var procs = Process.GetProcesses();
        try
        {
            UpdateSchedule();
            var names = procs.ToDictionary(p => p.Id, p => p.ProcessName);
            throttler.Ports.Refresh(names);
            var traffic = throttler.TakeTraffic();

            var alive = names.Values.Concat(settings.Limits.Keys).ToHashSet();
            foreach (var name in alive.Where(n => !byName.ContainsKey(n)))
            {
                if (!icons.TryGetValue(name, out var icon) || icon is null) icons[name] = icon = IconFor(procs, name);
                var row = new AppRow(name, icon, settings.Limits.GetValueOrDefault(name), OnLimitChanged);
                row.Init(settings.PriorityApps.Contains(name), settings.DataCaps.TryGetValue(name, out var cap) ? cap : null,
                    settings.AppProxies.GetValueOrDefault(name), settings.AppVpns.GetValueOrDefault(name));
                row.PropertyChanged += Row_PropertyChanged;
                byName[name] = row;
                rows.Add(row);
            }
            // Kurzlebige Programme (z. B. curl) noch 15 s nach dem letzten Verkehr zeigen, das ausgewählte nie entfernen – sonst kann man sie nicht anklicken
            foreach (var row in rows.Where(r => !alive.Contains(r.Name) && !r.IsActive && r != AppList.SelectedItem).ToList())
            {
                rows.Remove(row);
                byName.Remove(row.Name);
            }

            var today = DateOnly.FromDateTime(DateTime.Today);
            var sending = traffic.Keys.Select(k => k.Name).Distinct().ToList();
            foreach (var name in sending)
                usage.Add(today, name, traffic.GetValueOrDefault((name, false)), traffic.GetValueOrDefault((name, true)));
            CheckNewApps(sending);
            CheckCaps();
            UpdateGameMode(names.Values.ToHashSet());

            double Rate(string name, bool up) => traffic.GetValueOrDefault((name, up)) / 1024.0 / secs;
            foreach (var row in rows)
                row.Update(Rate(row.Name, false), Rate(row.Name, true));
            totalDownHistory = [.. totalDownHistory.Skip(1), rows.Sum(r => r.Down)];
            totalUpHistory = [.. totalUpHistory.Skip(1), rows.Sum(r => r.Up)];
            LimitedCount.Text = rows.Count(r => r.IsLimited).ToString();
            Reorder();
            UpdateGraph();
            UpdateDetail();

            ProxyStatus.Text = throttler.ProxyError is { } err ? "Letzter Fehler: " + err : "";
            UpdateVpnStatus();
            if (ticks % 5 == 0) _ = UpdatePingAsync();
            ticks++;
            if (UsageView.IsVisible && ticks % 5 == 0) RefreshUsage();
            if (ticks % 60 == 0) SaveUsage();
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    void OnLimitChanged(AppRow row)
    {
        var limit = row.Limit;
        if (limit == default) settings.Limits.Remove(row.Name);
        else settings.Limits[row.Name] = limit;
        throttler.SetLimit(row.Name, limit); // sofort wirksam, gespeichert wird gebündelt
        LimitedCount.Text = rows.Count(r => r.IsLimited).ToString();
        ScheduleSave();
    }

    void OnTotalLimitChanged(AppRow row)
    {
        settings.TotalLimit = row.Limit with { Blocked = false };
        throttler.SetTotalLimit(settings.TotalLimit);
        ScheduleSave();
    }

    void OnGameLimitChanged(AppRow row)
    {
        settings.GameModeLimit = row.Limit with { Blocked = false };
        ScheduleSave(); // wirksam beim nächsten Tick (UpdateGameMode)
    }

    void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not AppRow row) return;
        if (e.PropertyName == nameof(AppRow.IsPriority))
        {
            if (row.IsPriority) settings.PriorityApps.Add(row.Name);
            else settings.PriorityApps.Remove(row.Name);
            UpdatePriorityText();
            ScheduleSave();
        }
        else if (e.PropertyName == nameof(AppRow.VpnName))
        {
            if (row.VpnName is { } vpn) settings.AppVpns[row.Name] = vpn;
            else settings.AppVpns.Remove(row.Name);
            ApplyVpns();
            ScheduleSave();
        }
        else if (e.PropertyName == nameof(AppRow.ProxyName))
        {
            if (row.ProxyName is { } proxy) settings.AppProxies[row.Name] = proxy;
            else settings.AppProxies.Remove(row.Name);
            ApplyProxies();
            ScheduleSave();
        }
        else if (e.PropertyName == nameof(AppRow.Cap))
        {
            if (row.Cap is { } cap) settings.DataCaps[row.Name] = cap;
            else
            {
                settings.DataCaps.Remove(row.Name);
                row.CapStatus = "";
                row.CapShare = 0;
            }
            CheckCaps();
            ScheduleSave();
        }
    }

    void RaiseNotice(string title, string text, string? app) => Notice?.Invoke(title, text, app);

    /// <summary>Meldet Programme, die zum ersten Mal Daten senden.</summary>
    void CheckNewApps(IEnumerable<string> sending)
    {
        if (settings.KnownApps is null)
        {
            // Erste Erfassung: alles, was schon Verbrauch hat oder in den ersten 30 s sendet, gilt als bekannt – sonst käme eine Meldungsflut
            settings.KnownApps = usage.Days.Values.SelectMany(d => d.Keys).ToHashSet();
            seedUntil = DateTime.UtcNow.AddSeconds(30);
            ScheduleSave();
        }
        foreach (var name in sending)
        {
            if (!settings.KnownApps.Add(name)) continue;
            ScheduleSave();
            if (settings.NotifyNewApps && DateTime.UtcNow > seedUntil)
                RaiseNotice("Neues Programm im Internet", $"{name} sendet zum ersten Mal Daten. Klicken, um es zu drosseln oder zu sperren.", name);
        }
    }

    /// <summary>Datenlimits prüfen: Status anzeigen, bei 80 %/100 % melden, bei Bedarf sperren.</summary>
    void CheckCaps()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        Dictionary<string, (long Down, long Up)>? day = null, month = null;
        var blocked = new List<string>();
        foreach (var (name, cap) in settings.DataCaps)
        {
            var totals = cap.Monthly ? (month ??= usage.Totals(cap.PeriodStart(today), today)) : (day ??= usage.Totals(today, today));
            var (down, up) = totals.GetValueOrDefault(name);
            var used = down + up;
            var share = (double)used / cap.Bytes;
            var reached = share >= 1;
            if (reached && cap.Block) blocked.Add(name);
            if (byName.TryGetValue(name, out var row))
            {
                row.CapShare = Math.Min(share, 1);
                row.CapStatus = $"{AppRow.FormatBytes(used)} von {AppRow.FormatBytes(cap.Bytes)} {cap.PeriodText}" + (reached && cap.Block ? " · gesperrt" : "");
            }

            var key = $"{name}|{cap.PeriodKey(today)}";
            if (reached && notified.Add(key + "|100"))
                RaiseNotice($"Datenlimit erreicht: {name}", cap.Block
                    ? $"{AppRow.FormatBytes(cap.Bytes)} {cap.PeriodText} verbraucht – gesperrt bis {(cap.Monthly ? "Monatsende" : "Mitternacht")}."
                    : $"{AppRow.FormatBytes(cap.Bytes)} {cap.PeriodText} verbraucht.", name);
            else if (!reached && share >= DataCap.WarnAt && notified.Add(key + "|80"))
                RaiseNotice($"Datenlimit fast erreicht: {name}", $"{share:P0} von {AppRow.FormatBytes(cap.Bytes)} {cap.PeriodText} verbraucht.", name);
        }
        throttler.SetCapBlocked(blocked);
    }

    /// <summary>Spielmodus an, sobald ein Programm mit Vorrang läuft.</summary>
    void UpdateGameMode(HashSet<string> running)
    {
        var game = settings.GameModeEnabled ? settings.PriorityApps.FirstOrDefault(running.Contains) : null;
        throttler.SetGameMode(game is null ? null : settings.PriorityApps, settings.GameModeLimit);
        if (game == gameModeApp) return;
        if (game is not null && gameModeApp is null)
            RaiseNotice("Spielmodus aktiv", $"{game} läuft – alle anderen Programme sind zusammen gedrosselt.", null);
        gameModeApp = game;
        GameModePill.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        GameModeText.Text = $"Spielmodus · {game}";
    }

    // ---------- VPN pro Programm (WireGuard) ----------

    /// <summary>Tunnel für zugewiesene Configs starten, nicht mehr gebrauchte beenden, Zuordnung an den Throttler geben.</summary>
    void ApplyVpns()
    {
        var vpnByName = settings.Vpns.ToDictionary(v => v.Name);
        var needed = settings.AppVpns.Values.Where(vpnByName.ContainsKey).ToHashSet();
        foreach (var name in tunnels.Keys.Where(n => !needed.Contains(n)).ToList())
        {
            tunnels[name].Dispose();
            tunnels.Remove(name);
            lastTunnelState.Remove(name);
        }
        foreach (var name in needed.Where(n => !tunnels.ContainsKey(n)))
        {
            try
            {
                tunnels[name] = new WireGuardTunnel(name, vpnByName[name].Config());
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
            {
                vpnByName[name].Status = "Config fehlerhaft: " + ex.Message;
            }
        }
        try
        {
            throttler.SetVpns(settings.AppVpns.Where(a => tunnels.ContainsKey(a.Value)).ToDictionary(a => a.Key, a => tunnels[a.Value]));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Der VPN konnte nicht eingerichtet werden:\n{ex.Message}", "FlowCap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateVpnStatus();
    }

    /// <summary>Zustände anzeigen und bei Verbindungsverlust den Kill-Switch auslösen.</summary>
    void UpdateVpnStatus()
    {
        foreach (var entry in settings.Vpns)
        {
            if (!tunnels.TryGetValue(entry.Name, out var t))
            {
                if (!entry.Status.StartsWith("Config fehlerhaft")) entry.Status = "Keinem Programm zugewiesen";
                continue;
            }
            var state = t.State;
            entry.Status = state switch
            {
                TunnelState.Connected => $"● Verbunden mit {t.EndpointText} · ↓ {AppRow.FormatBytes(t.BytesReceived)}  ↑ {AppRow.FormatBytes(t.BytesSent)}",
                TunnelState.Connecting => $"○ Verbinde mit {t.EndpointText} …",
                _ => $"✕ Getrennt – {t.EndpointText} antwortet nicht. Zugewiesene Programme haben kein Internet.",
            };
            var before = lastTunnelState.GetValueOrDefault(entry.Name, TunnelState.Connecting);
            lastTunnelState[entry.Name] = state;
            if (state == TunnelState.Down && before != TunnelState.Down)
            {
                var killed = entry.KillSwitch ? KillAppsOf(entry.Name) : [];
                RaiseNotice($"VPN „{entry.Name}“ getrennt",
                    killed.Count > 0 ? "Kill-Switch: beendet " + string.Join(", ", killed) : "Die zugewiesenen Programme haben kein Internet, bis der Tunnel wieder steht.", null);
            }
        }
        foreach (var row in rows.Where(r => r.VpnName is not null))
            row.VpnStatus = settings.Vpns.FirstOrDefault(v => v.Name == row.VpnName)?.Status ?? "";
    }

    /// <summary>Alle laufenden Programme beenden, die diesem VPN zugewiesen sind. Liefert ihre Namen.</summary>
    List<string> KillAppsOf(string vpn)
    {
        var killed = new List<string>();
        foreach (var app in settings.AppVpns.Where(a => a.Value == vpn).Select(a => a.Key))
        {
            if (app.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var p in Process.GetProcessesByName(app))
            {
                try
                {
                    p.Kill();
                    killed.Add(app);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // schon beendet oder geschützt
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return killed.Distinct().ToList();
    }

    /// <summary>Beim Beenden von FlowCap: Programme mit Kill-Switch beenden – ohne FlowCap ginge ihr Verkehr sonst direkt ins Netz.</summary>
    public void KillVpnAppsOnExit()
    {
        foreach (var entry in settings.Vpns.Where(v => v.KillSwitch && tunnels.ContainsKey(v.Name))) KillAppsOf(entry.Name);
        foreach (var t in tunnels.Values) t.Dispose();
    }

    void ImportVpn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "WireGuard-Config laden",
            Filter = "WireGuard-Config (*.conf)|*.conf|Alle Dateien (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var added = new List<string>();
        foreach (var path in dialog.FileNames)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            for (var i = 2; settings.Vpns.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{System.IO.Path.GetFileNameWithoutExtension(path)} ({i})";
            try
            {
                var entry = VpnEntry.Create(name, System.IO.File.ReadAllText(path));
                entry.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(VpnEntry.KillSwitch)) ScheduleSave(); };
                settings.Vpns.Add(entry);
                VpnChoices.Add(name);
                added.Add(name);
            }
            catch (Exception ex) when (ex is FormatException or System.IO.IOException or UnauthorizedAccessException)
            {
                VpnHint.Text = $"✗ {System.IO.Path.GetFileName(path)}: {ex.Message}";
            }
        }
        if (added.Count > 0)
        {
            VpnHint.Text = $"✓ Geladen: {string.Join(", ", added)}. Zuweisen im Detailbereich eines Programms.";
            ScheduleSave();
        }
    }

    void DeleteVpn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VpnEntry entry }) return;
        if (MessageBox.Show(this, $"VPN „{entry.Name}“ entfernen? Zugewiesene Programme gehen danach wieder direkt ins Internet.", "FlowCap",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var row in rows.Where(r => r.VpnName == entry.Name)) row.VpnName = null;
        foreach (var app in settings.AppVpns.Where(a => a.Value == entry.Name).Select(a => a.Key).ToList()) settings.AppVpns.Remove(app);
        settings.Vpns.Remove(entry);
        VpnChoices.Remove(entry.Name);
        ApplyVpns();
        ScheduleSave();
    }

    // ---------- Proxy pro Programm ----------

    void ApplyProxies()
    {
        var byProxyName = settings.Proxies.ToDictionary(p => p.Name);
        var map = settings.AppProxies
            .Where(a => byProxyName.ContainsKey(a.Value))
            .ToDictionary(a => a.Key, a => byProxyName[a.Value]);
        try
        {
            throttler.SetProxies(map);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Das Proxy-Relay konnte nicht gestartet werden:\n{ex.Message}", "FlowCap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Proxy aus dem Formular in den Einstellungen; null + Hinweis bei ungültigen Angaben.</summary>
    ProxyServer? ProxyFromForm()
    {
        var name = ProxyNameBox.Text.Trim();
        var host = ProxyHostBox.Text.Trim();
        if (name.Length == 0 || host.Length == 0) ProxyHint.Text = "Bitte Name und Server angeben.";
        else if (!int.TryParse(ProxyPortBox.Text.Trim(), out var port) || port is < 1 or > 65535) ProxyHint.Text = "Port muss zwischen 1 und 65535 liegen.";
        else return ProxyServer.Create(name, ProxyTypeHttp.IsChecked == true ? ProxyType.Http : ProxyType.Socks5, host, port,
            ProxyUserBox.Text.Trim(), ProxyPasswordBox.Password);
        return null;
    }

    void AddProxy_Click(object sender, RoutedEventArgs e)
    {
        if (ProxyFromForm() is not { } proxy) return;
        if (settings.Proxies.Any(p => p.Name.Equals(proxy.Name, StringComparison.OrdinalIgnoreCase)))
        {
            ProxyHint.Text = "Einen Proxy mit diesem Namen gibt es schon.";
            return;
        }
        settings.Proxies.Add(proxy);
        ProxyChoices.Add(proxy.Name);
        ProxyNameBox.Text = ProxyHostBox.Text = ProxyPortBox.Text = ProxyUserBox.Text = "";
        ProxyPasswordBox.Password = "";
        ProxyHint.Text = $"„{proxy.Name}“ hinzugefügt. Zuweisen im Detailbereich eines Programms.";
        ScheduleSave();
    }

    async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        var proxy = (sender as FrameworkElement)?.DataContext as ProxyServer ?? ProxyFromForm();
        if (proxy is null) return;
        ProxyHint.Text = $"Teste „{proxy.Name}“ …";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Ein Verbindungsaufbau über den Proxy zu einem bekannten Ziel (Cloudflare, Port 443) zeigt, dass Server und Anmeldung stimmen
            await using var s = await ProxyHandshake.ConnectAsync(proxy, new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), timeout.Token);
            ProxyHint.Text = $"✓ „{proxy.Name}“ funktioniert.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            ProxyHint.Text = $"✗ „{proxy.Name}“: {(ex is OperationCanceledException ? "Zeitüberschreitung" : ex.Message)}";
        }
    }

    void DeleteProxy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProxyServer proxy }) return;
        foreach (var row in rows.Where(r => r.ProxyName == proxy.Name)) row.ProxyName = null;
        foreach (var app in settings.AppProxies.Where(a => a.Value == proxy.Name).Select(a => a.Key).ToList()) settings.AppProxies.Remove(app);
        settings.Proxies.Remove(proxy);
        ProxyChoices.Remove(proxy.Name);
        ApplyProxies();
        ScheduleSave();
    }

    // ---------- Profile & Zeitpläne ----------

    public string ActiveProfileName => settings.ActiveProfile;

    /// <summary>Manuelle Profilwahl (Kopfzeile, Tray): gilt sofort und übersteuert einen gerade laufenden Zeitplan bis zur nächsten Regel.</summary>
    public void SelectProfile(string name)
    {
        if (!settings.Profiles.ContainsKey(name)) return;
        settings.SelectedProfile = name;
        suppressedRule = currentRule;
        ScheduleBadge.Visibility = Visibility.Collapsed;
        ApplyProfile(name);
        ShowActiveProfile();
        ScheduleSave();
    }

    void ApplyProfile(string name)
    {
        if (name == settings.ActiveProfile || !settings.Profiles.TryGetValue(name, out var profile)) return;
        settings.Profiles[settings.ActiveProfile] = settings.Snapshot();
        settings.ActiveProfile = name;
        settings.Limits = new(profile.Limits);
        settings.TotalLimit = profile.TotalLimit;
        throttler.ReplaceLimits(settings.Limits);
        throttler.SetTotalLimit(settings.TotalLimit);
        foreach (var row in rows) row.ApplyLimit(settings.Limits.GetValueOrDefault(row.Name));
        totalRow.ApplyLimit(settings.TotalLimit);
        LimitedCount.Text = rows.Count(r => r.IsLimited).ToString();
        ShowActiveProfile();
        ScheduleSave();
    }

    void ShowActiveProfile()
    {
        showingProfile = true;
        ProfileBox.SelectedItem = settings.ActiveProfile;
        showingProfile = false;
    }

    void UpdateSchedule()
    {
        var rule = ScheduleRule.ActiveRule(settings.Schedules.Where(r => settings.Profiles.ContainsKey(r.Profile)), DateTime.Now);
        if (rule != currentRule)
        {
            currentRule = rule;
            suppressedRule = null; // neue Regel greift wieder, auch nach manueller Wahl
        }
        var scheduled = rule is not null && rule != suppressedRule;
        ScheduleBadge.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
        var target = scheduled ? rule!.Profile : settings.SelectedProfile;
        if (target == settings.ActiveProfile) return;
        ApplyProfile(target);
        if (scheduled) RaiseNotice($"Profil „{target}“ aktiv", "Umgeschaltet nach Zeitplan.", null);
    }

    void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!showingProfile && ProfileBox.SelectedItem is string name && name != settings.ActiveProfile) SelectProfile(name);
    }

    void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = NewProfileName.Text.Trim();
        if (name.Length == 0 || settings.Profiles.ContainsKey(name))
        {
            NewProfileHint.Text = name.Length == 0 ? "Bitte einen Namen eingeben." : "Ein Profil mit diesem Namen gibt es schon.";
            return;
        }
        settings.Profiles[name] = settings.Snapshot(); // startet als Kopie der aktuellen Limits
        var index = ProfileNames.TakeWhile(n => string.Compare(n, name, StringComparison.OrdinalIgnoreCase) < 0).Count();
        ProfileNames.Insert(index, name);
        NewProfileName.Text = "";
        NewProfileHint.Text = $"„{name}“ angelegt und aktiviert – Änderungen an Limits werden jetzt darin gespeichert.";
        SelectProfile(name);
    }

    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string name } || ProfileNames.Count <= 1) return;
        if (MessageBox.Show(this, $"Profil „{name}“ löschen? Zeitpläne für dieses Profil werden ebenfalls entfernt.", "FlowCap",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (name == settings.ActiveProfile) SelectProfile(ProfileNames.First(n => n != name));
        settings.Profiles.Remove(name);
        ProfileNames.Remove(name);
        if (settings.SelectedProfile == name) settings.SelectedProfile = settings.ActiveProfile;
        foreach (var rule in settings.Schedules.Where(r => r.Profile == name).ToList()) RemoveRule(rule);
        ScheduleSave();
    }

    void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        var rule = new ScheduleRule { Profile = ProfileNames.FirstOrDefault(n => n != settings.SelectedProfile) ?? ProfileNames[0] };
        rule.PropertyChanged += Rule_PropertyChanged;
        settings.Schedules.Add(rule);
        ScheduleSave();
    }

    void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleRule rule }) RemoveRule(rule);
        ScheduleSave();
    }

    void RemoveRule(ScheduleRule rule)
    {
        rule.PropertyChanged -= Rule_PropertyChanged;
        settings.Schedules.Remove(rule);
    }

    void Rule_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ScheduleSave();

    void UpdatePriorityText() =>
        PriorityText.Text = settings.PriorityApps.Count == 0
            ? "Noch kein Programm hat Vorrang. Markiere dein Spiel im Detailbereich mit „Vorrang im Spielmodus“."
            : "Vorrang: " + string.Join(", ", settings.PriorityApps.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>Programm in der Live-Ansicht auswählen (z. B. nach Klick auf eine Meldung).</summary>
    public void SelectApp(string name)
    {
        LiveTab.IsChecked = true;
        if (!byName.TryGetValue(name, out var row)) return;
        if (!visible.Contains(row)) visible.Add(row);
        AppList.SelectedItem = row;
        AppList.ScrollIntoView(row);
    }

    void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>Einstellungen und Verbrauch sofort schreiben (Timer und Programmende).</summary>
    public void SaveNow()
    {
        saveTimer.Stop();
        try
        {
            settings.Profiles[settings.ActiveProfile] = settings.Snapshot(); // Änderungen gehören zum aktiven Profil
            settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Einstellungen konnten nicht gespeichert werden:\n{ex.Message}", "FlowCap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void SaveUsage()
    {
        try
        {
            usage.Save();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // Daten bleiben im Speicher, nächster Versuch in einer Minute
        }
    }

    // ---------- Pause & Tray ----------

    public bool IsPaused => throttler.Paused;

    public void SetPaused(bool paused)
    {
        throttler.Paused = paused;
        PauseToggle.IsChecked = paused;
        PauseText.Text = paused ? "Pausiert" : "Pausieren";
    }

    void Pause_Click(object sender, RoutedEventArgs e) => SetPaused(PauseToggle.IsChecked == true);

    /// <summary>Für das Tray-Menü: die aktivsten Programme.</summary>
    public IEnumerable<AppRow> TopRows(int count) =>
        rows.Where(r => r.IsActive).OrderByDescending(r => r.Total).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(count);

    // ---------- Live-Ansicht ----------

    void Search_Changed(object sender, TextChangedEventArgs e) => Reorder();

    void Filter_Changed(object sender, RoutedEventArgs e) => Reorder();

    void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGraph();
        UpdateDetail();
    }

    void ClearLimit_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as AppRow)?.ClearLimits();

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AppRow row, Tag: string tag }) return; // Tag: "d:1024" oder "u:1024"
        var kbs = int.Parse(tag[2..]);
        if (tag[0] == 'd') row.LimitDown = kbs;
        else row.LimitUp = kbs;
    }

    void UpdateGraph()
    {
        if (MainGraph is null) return;
        var row = AppList.SelectedItem as AppRow;
        var n = settings.GraphSeconds;
        MainGraph.Down = (row?.DownHistory ?? totalDownHistory)[^n..];
        MainGraph.Up = (row?.UpHistory ?? totalUpHistory)[^n..];
        var span = n < 120 ? $"{n} SEKUNDEN" : $"{n / 60} MINUTEN";
        GraphTitle.Text = (row is null ? "GESAMT" : row.Name.ToUpperInvariant()) + " · LETZTE " + span;
        CurrentDown.Text = AppRow.FormatRate(MainGraph.Down[^1]);
        CurrentUp.Text = AppRow.FormatRate(MainGraph.Up[^1]);
        TotalGauge.Value = MainGraph.Down[^1] + MainGraph.Up[^1];
        TotalGauge.Maximum = Math.Max(10, MainGraph.Down.Zip(MainGraph.Up, (d, u) => d + u).Max());
    }

    /// <summary>Tagesverbrauch und Verbindungen des ausgewählten Programms.</summary>
    void UpdateDetail()
    {
        if (AppList.SelectedItem is not AppRow row) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (down, up) = usage.Totals(today, today).GetValueOrDefault(row.Name);
        row.UsageToday = $"↓ {AppRow.FormatBytes(down)}   ↑ {AppRow.FormatBytes(up)}";

        var conns = throttler.Ports.ConnectionsOf(row.Name).Distinct().Take(50).ToList();
        foreach (var ip in conns.Select(c => c.Remote).Distinct()) ResolveHost(ip);
        var desired = conns
            .Select(c => new ConnRow(hostNames.GetValueOrDefault(c.Remote) ?? c.Remote.ToString(), $"{Endpoint(c)} · {c.State}"))
            .Distinct()
            .OrderBy(c => c.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CollectionSync.SyncTo(connRows, desired);
        ConnHeader.Text = $"VERBINDUNGEN ({desired.Count})";
        ConnEmpty.Visibility = desired.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Endpoint(Connection c) =>
        c.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{c.Remote}]:{c.RemotePort}" : $"{c.Remote}:{c.RemotePort}";

    // Hostname per Reverse-DNS im Hintergrund nachladen; bis dahin (oder bei Fehler) wird die IP angezeigt
    void ResolveHost(IPAddress ip)
    {
        if (!hostNames.TryAdd(ip, ip.ToString())) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(4));
                if (!string.IsNullOrEmpty(entry.HostName)) hostNames[ip] = entry.HostName;
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or TimeoutException or ArgumentException)
            {
            }
        });
    }

    void Reorder()
    {
        if (AppList is null || ActiveOnly is null) return; // Events feuern schon während InitializeComponent
        var query = SearchBox.Text.Trim();
        var selected = AppList.SelectedItem;
        var desired = rows
            .Where(r => r == selected || ((ActiveOnly.IsChecked != true || r.IsActive) && r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Total).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CollectionSync.SyncTo(visible, desired);
    }

    // ---------- Verbrauch ----------

    void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (LiveView is null || UsageView is null || SpeedView is null) return;
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        LiveView.Visibility = Show(LiveTab.IsChecked == true);
        UsageView.Visibility = Show(UsageTab.IsChecked == true);
        SpeedView.Visibility = Show(SpeedTab.IsChecked == true);
        if (UsageTab.IsChecked == true) RefreshUsage();
    }

    // ---------- Ping & Speedtest ----------

    bool pinging;
    CancellationTokenSource? speedCts;

    async Task UpdatePingAsync()
    {
        if (pinging) return; // vorheriger Ping hängt noch (max. 2 s)
        pinging = true;
        var ms = await SpeedTest.PingOnceAsync();
        PingText.Text = ms is { } v ? $"{v:0} ms" : "keine Antwort";
        pinging = false;
    }

    async void Speedtest_Click(object sender, RoutedEventArgs e)
    {
        if (speedCts is not null)
        {
            speedCts.Cancel(); // zweiter Klick = abbrechen
            return;
        }
        speedCts = new CancellationTokenSource();
        var ct = speedCts.Token;
        SpeedButton.Content = "Abbrechen";
        SpeedError.Text = "";
        SpeedPing.Text = SpeedJitter.Text = SpeedDown.Text = SpeedUp.Text = "–";
        var top = 0.0;
        void Show(double mbps, string caption)
        {
            top = Math.Max(top, mbps);
            SpeedGauge.Maximum = Math.Max(100, Math.Ceiling(top * 1.2 / 50) * 50); // Skala wächst mit, in 50er-Schritten
            SpeedGauge.Value = mbps;
            SpeedGauge.Label = $"{mbps:0.0} Mbit/s";
            SpeedGauge.Caption = caption;
        }

        try
        {
            SpeedPhase.Text = "Ping wird gemessen …";
            Show(0, "Ping");
            var (ping, jitter) = await SpeedTest.PingAsync(10, ct);
            SpeedPing.Text = $"{ping:0} ms";
            SpeedJitter.Text = $"{jitter:0} ms";

            SpeedPhase.Text = "Download wird gemessen …";
            var down = await SpeedTest.DownloadAsync(new Progress<double>(m => Show(m, "Download")), ct);
            SpeedDown.Text = $"{down:0.0} Mbit/s";

            top = 0;
            SpeedPhase.Text = "Upload wird gemessen …";
            var up = await SpeedTest.UploadAsync(new Progress<double>(m => Show(m, "Upload")), ct);
            SpeedUp.Text = $"{up:0.0} Mbit/s";

            Show(down, "Download");
            SpeedPhase.Text = "Fertig";
            settings.SpeedTests.Insert(0, new SpeedResult(DateTime.Now, ping, jitter, down, up));
            while (settings.SpeedTests.Count > 20) settings.SpeedTests.RemoveAt(settings.SpeedTests.Count - 1);
            ScheduleSave();
        }
        catch (OperationCanceledException)
        {
            SpeedPhase.Text = "Abgebrochen";
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException)
        {
            SpeedPhase.Text = "";
            SpeedError.Text = "Test fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            speedCts.Dispose();
            speedCts = null;
            SpeedButton.Content = "Test starten";
        }
    }

    void Period_Changed(object sender, RoutedEventArgs e)
    {
        if (UsageList is not null) RefreshUsage();
    }

    void RefreshUsage()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = PeriodToday.IsChecked == true ? 1 : PeriodWeek.IsChecked == true ? 7 : 30;
        var totals = usage.Totals(today.AddDays(1 - days), today);
        var max = Math.Max(1, totals.Values.Select(t => t.Down + t.Up).DefaultIfEmpty(0).Max());
        UsageList.ItemsSource = totals
            .OrderByDescending(t => t.Value.Down + t.Value.Up)
            .Select(t => new UsageRow(t.Key, icons.GetValueOrDefault(t.Key),
                AppRow.FormatBytes(t.Value.Down), AppRow.FormatBytes(t.Value.Up), AppRow.FormatBytes(t.Value.Down + t.Value.Up),
                (double)t.Value.Down / max, (double)(t.Value.Down + t.Value.Up) / max))
            .ToList();
        long sumDown = totals.Values.Sum(t => t.Down), sumUp = totals.Values.Sum(t => t.Up);
        UsageSummary.Text = $"Gesamt ↓ {AppRow.FormatBytes(sumDown)}   ↑ {AppRow.FormatBytes(sumUp)}   ·   {totals.Count} Programme";
        UsageEmpty.Visibility = totals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Einstellungen ----------

    void OpenSettings_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Visible;

    void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // Klick in die Karte schließt nicht

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsOverlay.IsVisible)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    void Unit_Changed(object sender, RoutedEventArgs e)
    {
        if (totalRow is null) return; // während InitializeComponent
        settings.UseBits = AppRow.UseBits = UnitBits.IsChecked == true;
        foreach (var r in rows) r.RefreshTexts();
        totalRow.RefreshTexts();
        UpdateGraph();
        MainGraph.InvalidateVisual();
        TotalGauge.InvalidateVisual();
        ScheduleSave();
    }

    void Range_Changed(object sender, RoutedEventArgs e)
    {
        if (totalRow is null || sender is not FrameworkElement { Tag: string tag }) return;
        settings.GraphSeconds = int.Parse(tag);
        UpdateGraph();
        ScheduleSave();
    }

    void GameMode_Click(object sender, RoutedEventArgs e)
    {
        settings.GameModeEnabled = GameModeBox.IsChecked == true;
        ScheduleSave();
    }

    void NotifyNewApps_Click(object sender, RoutedEventArgs e)
    {
        settings.NotifyNewApps = NotifyNewAppsBox.IsChecked == true;
        ScheduleSave();
    }

    void StartInTray_Click(object sender, RoutedEventArgs e)
    {
        settings.StartInTray = StartInTrayBox.IsChecked == true;
        ScheduleSave();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.Set(AutostartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FlowCap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        AutostartBox.IsChecked = Autostart.IsEnabled;
    }

    // ---------- Icons ----------

    static ImageSource? IconFor(Process[] procs, string name)
    {
        foreach (var p in procs.Where(p => p.ProcessName == name))
        {
            var path = ImagePath(p.Id);
            if (path is null) continue;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is null) continue;
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch (Exception e) when (e is System.IO.IOException or ArgumentException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    // Schneller und bei geschützten Prozessen zuverlässiger als Process.MainModule
    static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
