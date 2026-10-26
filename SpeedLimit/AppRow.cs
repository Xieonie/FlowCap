using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SpeedLimit;

public sealed class AppRow(string name, ImageSource? icon, Limit limit, Action<AppRow> limitChanged) : INotifyPropertyChanged
{
    /// <summary>15 Minuten à 1 Sekunde – reicht für den längsten Graph-Zeitraum.</summary>
    public const int HistoryLength = 900;
    const int SparklineLength = 60;
    static readonly TimeSpan ActiveFor = TimeSpan.FromSeconds(15);

    /// <summary>Raten in Mbit/s statt KB/s anzeigen (Einstellung).</summary>
    public static bool UseBits { get; set; }

    double down, up;
    double[] downHistory = new double[HistoryLength], upHistory = new double[HistoryLength];
    double[] downRecent = new double[SparklineLength], upRecent = new double[SparklineLength];
    DateTime lastTraffic = DateTime.MinValue;
    int? limitDown = limit.DownKBs > 0 ? limit.DownKBs : null;
    int? limitUp = limit.UpKBs > 0 ? limit.UpKBs : null;
    bool blocked = limit.Blocked;
    // Zuletzt gewählter Wert, damit "Begrenzen" an/aus den Schieberegler nicht zurücksetzt
    int lastDown = limit.DownKBs > 0 ? limit.DownKBs : 1024;
    int lastUp = limit.UpKBs > 0 ? limit.UpKBs : 1024;
    string usageToday = "";

    public string Name { get; } = name;
    /// <summary>Anfangsbuchstabe als Ersatz-Icon (Dienste, Konsolenprogramme, System).</summary>
    public string Initial => Initials(Name);

    public static string Initials(string name) => name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
    public ImageSource? Icon { get; } = icon;

    /// <summary>Aktuelle Rate in KB/s.</summary>
    public double Down => down;
    public double Up => up;
    public double Total => down + up;
    public string DownText => FormatRate(down);
    public string UpText => FormatRate(up);
    /// <summary>Volle Historie für den großen Graphen.</summary>
    public double[] DownHistory => downHistory;
    public double[] UpHistory => upHistory;
    /// <summary>Letzte Minute für die Sparkline in der Liste.</summary>
    public double[] DownRecent => downRecent;
    public double[] UpRecent => upRecent;

    /// <summary>Hatte in den letzten Sekunden Verkehr oder hat ein Limit.</summary>
    public bool IsActive => IsLimited || DateTime.UtcNow - lastTraffic < ActiveFor;

    public int? LimitDown
    {
        get => limitDown;
        set { limitDown = value > 0 ? value : null; if (limitDown is { } v) lastDown = v; LimitsUpdated(); }
    }

    public int? LimitUp
    {
        get => limitUp;
        set { limitUp = value > 0 ? value : null; if (limitUp is { } v) lastUp = v; LimitsUpdated(); }
    }

    public bool DownLimited { get => limitDown is not null; set => LimitDown = value ? lastDown : null; }
    public bool UpLimited { get => limitUp is not null; set => LimitUp = value ? lastUp : null; }

    public bool IsBlocked { get => blocked; set { blocked = value; LimitsUpdated(); } }
    public bool IsNotBlocked => !blocked;

    /// <summary>Schieberegler-Position 0–1000 (logarithmisch).</summary>
    public double DownSlider { get => KBsToSlider(limitDown ?? lastDown); set => LimitDown = SliderToKBs(value); }
    public double UpSlider { get => KBsToSlider(limitUp ?? lastUp); set => LimitUp = SliderToKBs(value); }

    public string LimitDownText => limitDown is { } d ? FormatLimit(d) : "Unbegrenzt";
    public string LimitUpText => limitUp is { } u ? FormatLimit(u) : "Unbegrenzt";

    /// <summary>Hat irgendeine Einschränkung (Limit oder Sperre) – wird gespeichert und hervorgehoben.</summary>
    public bool IsLimited => blocked || limitDown is not null || limitUp is not null;
    public Limit Limit => new(limitDown ?? 0, limitUp ?? 0, blocked);

    /// <summary>Kurzform für das Abzeichen in der Liste, z. B. "↓ 5 MB/s  ↑ 1 MB/s" oder "Gesperrt".</summary>
    public string Badge => blocked ? "Gesperrt" : string.Join("  ", new[]
    {
        limitDown is { } d ? "↓ " + FormatLimit(d) : null,
        limitUp is { } u ? "↑ " + FormatLimit(u) : null,
        vpnName is not null ? "VPN" : proxyName is not null ? "Proxy" : null, // VPN hat Vorrang vor dem Proxy
    }.Where(s => s is not null));

    // ---------- Spielmodus-Vorrang und Datenlimit (Änderungen meldet PropertyChanged, das Hauptfenster speichert) ----------

    public static readonly long[] CapPresets = [100L << 20, 500L << 20, 1L << 30, 5L << 30, 10L << 30, 50L << 30, 100L << 30];
    public static readonly string[] CapPresetNames = CapPresets.Select(FormatBytes).ToArray();
    public static readonly string[] CapPeriodNames = ["pro Tag", "pro Monat"];

    bool priority, capEnabled, capMonthly = true, capBlock;
    long capBytes = 5L << 30;
    string capStatus = "";
    double capShare;

    public const string NoProxy = "Kein Proxy";
    string? proxyName;

    /// <summary>Zugewiesener Proxy oder null.</summary>
    public string? ProxyName
    {
        get => proxyName;
        set
        {
            proxyName = value;
            foreach (var p in new[] { nameof(ProxyName), nameof(ProxyChoice), nameof(Badge), nameof(HasBadge) }) Notify(p);
        }
    }

    /// <summary>Für die Auswahlliste: Proxy-Name oder "Kein Proxy".</summary>
    public string ProxyChoice { get => proxyName ?? NoProxy; set => ProxyName = value is null or NoProxy ? null : value; }

    public const string NoVpn = "Kein VPN";
    string? vpnName;
    string vpnStatus = "";

    /// <summary>Zugewiesener WireGuard-Tunnel oder null.</summary>
    public string? VpnName
    {
        get => vpnName;
        set
        {
            vpnName = value;
            foreach (var p in new[] { nameof(VpnName), nameof(VpnChoice), nameof(Badge), nameof(HasBadge) }) Notify(p);
        }
    }

    public string VpnChoice { get => vpnName ?? NoVpn; set => VpnName = value is null or NoVpn ? null : value; }

    /// <summary>Zustand des zugewiesenen Tunnels (setzt das Hauptfenster).</summary>
    public string VpnStatus { get => vpnStatus; set { if (value != vpnStatus) { vpnStatus = value; Notify(); } } }

    /// <summary>Abzeichen in der Liste zeigen (Limit, Sperre, Proxy oder VPN).</summary>
    public bool HasBadge => IsLimited || proxyName is not null || vpnName is not null;

    /// <summary>Startwerte aus den Einstellungen setzen, ohne Änderungen zu melden.</summary>
    public void Init(bool isPriority, DataCap? cap, string? proxy = null, string? vpn = null)
    {
        priority = isPriority;
        proxyName = proxy;
        vpnName = vpn;
        if (cap is not { } c) return;
        (capEnabled, capBytes, capMonthly, capBlock) = (true, c.Bytes, c.Monthly, c.Block);
    }

    public bool IsPriority { get => priority; set { priority = value; Notify(); } }

    bool uploadAlarmOff;
    /// <summary>Keine Warnung bei viel Upload (z. B. Streaming, Backups).</summary>
    public bool UploadAlarmOff { get => uploadAlarmOff; set { uploadAlarmOff = value; Notify(); } }

    public DataCap? Cap => capEnabled ? new DataCap(capBytes, capMonthly, capBlock) : null;
    public bool CapEnabled { get => capEnabled; set { capEnabled = value; CapChanged(); } }
    public int CapPresetIndex
    {
        get => Math.Max(0, Array.IndexOf(CapPresets, capBytes));
        set { if (value >= 0) { capBytes = CapPresets[value]; CapChanged(); } }
    }
    public int CapPeriodIndex { get => capMonthly ? 1 : 0; set { capMonthly = value == 1; CapChanged(); } }
    public bool CapBlock { get => capBlock; set { capBlock = value; CapChanged(); } }

    /// <summary>z. B. "1,2 GB von 5 GB diesen Monat" (setzt das Hauptfenster).</summary>
    public string CapStatus { get => capStatus; set { if (value != capStatus) { capStatus = value; Notify(); } } }
    /// <summary>Verbrauchter Anteil 0–1.</summary>
    public double CapShare { get => capShare; set { if (value != capShare) { capShare = value; Notify(); } } }

    void CapChanged()
    {
        foreach (var p in new[] { nameof(CapEnabled), nameof(CapPresetIndex), nameof(CapPeriodIndex), nameof(CapBlock) })
            Notify(p);
        Notify(nameof(Cap)); // zuletzt: darauf reagiert das Hauptfenster
    }

    /// <summary>Verbrauch heute, z. B. "↓ 1,2 GB  ↑ 40 MB" (setzt das Hauptfenster).</summary>
    public string UsageToday { get => usageToday; set { if (value != usageToday) { usageToday = value; Notify(); } } }

    public void Update(double downKBs, double upKBs)
    {
        down = downKBs;
        up = upKBs;
        downHistory = [.. downHistory.Skip(1), downKBs]; // neues Array, damit die Graph-Bindung neu zeichnet
        upHistory = [.. upHistory.Skip(1), upKBs];
        downRecent = downHistory[^SparklineLength..];
        upRecent = upHistory[^SparklineLength..];
        if (downKBs + upKBs >= 0.05) lastTraffic = DateTime.UtcNow;
        foreach (var p in new[] { nameof(Down), nameof(Up), nameof(Total), nameof(DownText), nameof(UpText), nameof(DownHistory), nameof(UpHistory),
                                  nameof(DownRecent), nameof(UpRecent), nameof(IsActive) })
            Notify(p);
    }

    public void ClearLimits()
    {
        limitDown = limitUp = null;
        blocked = false;
        LimitsUpdated();
    }

    /// <summary>Nach Umschalten der Einheit alle Texte neu anzeigen.</summary>
    public void RefreshTexts()
    {
        foreach (var p in new[] { nameof(DownText), nameof(UpText), nameof(LimitDownText), nameof(LimitUpText), nameof(Badge) })
            Notify(p);
    }

    /// <summary>Limit von außen übernehmen (Profilwechsel), ohne es als Änderung des Nutzers zurückzumelden.</summary>
    public void ApplyLimit(Limit limit)
    {
        limitDown = limit.DownKBs > 0 ? limit.DownKBs : null;
        limitUp = limit.UpKBs > 0 ? limit.UpKBs : null;
        blocked = limit.Blocked;
        if (limitDown is { } d) lastDown = d;
        if (limitUp is { } u) lastUp = u;
        NotifyLimits();
    }

    void LimitsUpdated()
    {
        NotifyLimits();
        limitChanged(this);
    }

    void NotifyLimits()
    {
        foreach (var p in new[] { nameof(LimitDown), nameof(LimitUp), nameof(DownLimited), nameof(UpLimited), nameof(DownSlider), nameof(UpSlider),
                                  nameof(LimitDownText), nameof(LimitUpText), nameof(IsLimited), nameof(IsBlocked), nameof(IsNotBlocked),
                                  nameof(Badge), nameof(HasBadge), nameof(IsActive) })
            Notify(p);
    }

    // 0 → 10 KB/s, 1000 → 100 000 KB/s; gerundet auf "glatte" Werte (2 signifikante Stellen in KB/s bzw. MB/s)
    public static int SliderToKBs(double slider)
    {
        var kbs = Math.Round(10 * Math.Pow(10, Math.Clamp(slider, 0, 1000) / 250), 6); // 1023,9999… soll 1024 sein
        return kbs < 1024 ? (int)Significant2(kbs) : (int)Math.Round(Significant2(kbs / 1024) * 1024);
    }

    public static double KBsToSlider(int kbs) => Math.Clamp(Math.Log10(Math.Max(kbs, 10) / 10.0) * 250, 0, 1000);

    static double Significant2(double x)
    {
        var mag = Math.Pow(10, Math.Floor(Math.Log10(x)) - 1);
        return Math.Round(x / mag) * mag;
    }

    public static string FormatLimit(int kbs) => UseBits
        ? FormatBits(kbs)
        : kbs < 1024 ? $"{kbs} KB/s" : $"{kbs / 1024.0:0.##} MB/s";

    public static string FormatRate(double kbs) => UseBits
        ? FormatBits(kbs)
        : kbs switch
        {
            < 0.05 => "0 KB/s",
            < 1000 => $"{kbs:0.#} KB/s",
            _ => $"{kbs / 1024:0.0#} MB/s",
        };

    static string FormatBits(double kbs)
    {
        var bits = kbs * 1024 * 8;
        return bits switch
        {
            < 50 => "0 kbit/s",
            < 1_000_000 => $"{bits / 1000:0.#} kbit/s",
            _ => $"{bits / 1_000_000:0.0#} Mbit/s",
        };
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? prop = null) => PropertyChanged?.Invoke(this, new(prop));
}
