using System.Windows;
using Forms = System.Windows.Forms;

namespace SpeedLimit;

public partial class App : Application
{
    Mutex? singleInstance;
    Throttler? throttler;
    Forms.NotifyIcon? tray;
    bool exiting, trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        singleInstance = new Mutex(true, @"Local\FlowCap", out var first);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FlowCap.Show");
        if (!first)
        {
            showSignal.Set(); // laufende Instanz nach vorne holen
            Shutdown();
            return;
        }

        var settings = Settings.Load();
        try
        {
            throttler = new Throttler(settings.Limits, settings.TotalLimit);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "FlowCap", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var win = new MainWindow(throttler, settings, UsageStore.Load());
        MainWindow = win;
        win.Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true;
            win.Hide();
            ShowTrayHint();
        };

        var menu = new Forms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = true };
        menu.Opening += (_, _) => BuildMenu(menu, win);
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "FlowCap",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) win.ShowFromTray(); };

        // Meldungen (Datenlimit, neues Programm, Spielmodus); Klick öffnet FlowCap beim betroffenen Programm
        string? noticeApp = null;
        win.Notice += (title, text, app) =>
        {
            noticeApp = app;
            tray.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
        };
        tray.BalloonTipClicked += (_, _) =>
        {
            win.ShowFromTray();
            if (noticeApp is not null) win.SelectApp(noticeApp);
        };

        win.Start();

        new Thread(() => { while (showSignal.WaitOne()) Dispatcher.Invoke(win.ShowFromTray); }) { IsBackground = true }.Start();

        if (e.Args.Contains("--tray")) return; // Autostart: still im Tray
        if (settings.StartInTray) ShowTrayHint();
        else win.Show();
    }

    void ShowTrayHint()
    {
        if (trayHintShown) return;
        tray!.ShowBalloonTip(3000, "FlowCap", "Läuft im Infobereich weiter. Rechtsklick auf das Symbol für das Schnellmenü.", Forms.ToolTipIcon.Info);
        trayHintShown = true;
    }

    // Wird bei jedem Öffnen neu gebaut, damit Pause-Status und aktivste Programme aktuell sind
    void BuildMenu(Forms.ContextMenuStrip menu, MainWindow win)
    {
        menu.Items.Clear();
        menu.Items.Add("FlowCap öffnen", null, (_, _) => win.ShowFromTray()).Font = new(menu.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Alle Limits pausieren", null, (_, _) => win.SetPaused(!win.IsPaused)) { Checked = win.IsPaused });
        var profiles = new Forms.ToolStripMenuItem($"Profil: {win.ActiveProfileName}");
        foreach (var name in win.ProfileNames.ToList())
            profiles.DropDownItems.Add(new Forms.ToolStripMenuItem(name, null, (_, _) => win.SelectProfile(name)) { Checked = name == win.ActiveProfileName });
        menu.Items.Add(profiles);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var top = win.TopRows(5).ToList();
        menu.Items.Add(new Forms.ToolStripMenuItem(top.Count == 0 ? "Gerade keine aktiven Programme" : "Aktivste Programme") { Enabled = false });
        foreach (var row in top)
        {
            var item = new Forms.ToolStripMenuItem($"{row.Name}   ↓ {row.DownText}");
            foreach (var kbs in new[] { 500, 1024, 5120 })
                item.DropDownItems.Add(new Forms.ToolStripMenuItem($"Download auf {AppRow.FormatLimit(kbs)} begrenzen", null, (_, _) => row.LimitDown = kbs)
                    { Checked = row.LimitDown == kbs });
            item.DropDownItems.Add(new Forms.ToolStripSeparator());
            item.DropDownItems.Add(new Forms.ToolStripMenuItem("Internet sperren", null, (_, _) => row.IsBlocked = !row.IsBlocked) { Checked = row.IsBlocked });
            item.DropDownItems.Add(new Forms.ToolStripMenuItem("Alle Einschränkungen entfernen", null, (_, _) => row.ClearLimits()) { Enabled = row.IsLimited });
            menu.Items.Add(item);
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Quit());
    }

    void Quit()
    {
        exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is SpeedLimit.MainWindow win)
        {
            win.SaveNow(); // noch nicht gespeicherte Schieberegler-Änderung sichern
            win.SaveUsage();
            win.KillVpnAppsOnExit();
        }
        throttler?.Dispose();
        tray?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
