# Screenshots der wichtigsten Ansichten für die README.
# Muss als Administrator laufen: FlowCap läuft erhöht, und nur ein ebenso erhöhter Prozess darf es per UI Automation bedienen.
param(
    [string]$Exe = "$env:ProgramFiles\FlowCap\FlowCap.exe",
    [string]$Out = (Join-Path $PSScriptRoot '..\docs\screenshots'),
    [string]$Log = (Join-Path $env:TEMP 'flowcap-screenshots.log')
)
$ErrorActionPreference = 'Stop'
Start-Transcript -Path $Log -Force | Out-Null
try {
    # Ohne Adminrechte sieht man FlowCaps Fenster, aber keinen Inhalt darin (Windows schützt erhöhte Prozesse)
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw 'Bitte in einer PowerShell "Als Administrator ausführen" starten.' }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
'@
    [Win]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null # Per-Monitor v2: echte Pixel statt skalierter

    $null = New-Item -ItemType Directory -Force $Out
    Start-Process $Exe # startet FlowCap oder holt das laufende Fenster aus dem Tray nach vorne
    $A = [System.Windows.Automation.AutomationElement]
    # Nicht MainWindowHandle nehmen: das kann das unsichtbare Tray-Fenster sein. Gesucht ist das Fenster mit den Reitern.
    $liveTab = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'LiveTab')
    $win = $null
    for ($i = 0; $i -lt 40 -and -not $win; $i++) {
        Start-Sleep -Milliseconds 500
        foreach ($p in Get-Process FlowCap -ErrorAction SilentlyContinue) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)
            foreach ($w in $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
                if ($i -eq 0) { "Fenster: '$($w.Current.Name)' Klasse $($w.Current.ClassName)" }
                # Ein verstecktes Fenster (im Tray) zeigt keine Inhalte – sichtbar machen
                [Win]::ShowWindow([IntPtr]$w.Current.NativeWindowHandle, 9) | Out-Null
                if ($w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $liveTab)) { $win = $w; break }
                if ($i -eq 39) {
                    'Inhalt des Fensters:'
                    $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                        Select-Object -First 60 | ForEach-Object { "  $($_.Current.ControlType.ProgrammaticName) '$($_.Current.Name)' id=$($_.Current.AutomationId)" }
                }
            }
        }
    }
    if (-not $win) { throw 'FlowCap-Hauptfenster nicht gefunden (ist es minimiert oder im Tray?).' }
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    # Maximiert wären die Bilder riesig und leer – auf normale Größe zurück, dann feste Größe
    $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    Start-Sleep 1
    [Win]::MoveWindow($hwnd, 40, 40, 1920, 1080, $true) | Out-Null # Full HD: groß genug für Details, ohne leer zu wirken
    Start-Sleep 2

    function Find($id, $name) {
        $prop = if ($id) { $A::AutomationIdProperty } else { $A::NameProperty }
        $cond = New-Object System.Windows.Automation.PropertyCondition($prop, $(if ($id) { $id } else { $name }))
        $e = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if (-not $e) {
            $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            $all | Select-Object -First 80 | ForEach-Object { "  $($_.Current.ControlType.ProgrammaticName) '$($_.Current.Name)' id=$($_.Current.AutomationId)" }
            throw "Element nicht gefunden: $id$name"
        }
        $e
    }
    function Select-Tab($id) { (Find $id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 2 }
    function Press($id, $name) { (Find $id $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    function Shot($file) {
        [Win]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 700
        $r = New-Object Win+RECT
        [Win]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) | Out-Null # DWMWA_EXTENDED_FRAME_BOUNDS: ohne unsichtbaren Rand
        $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
        $bmp.Save((Join-Path $Out $file), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        "gespeichert: $file"
    }

    Select-Tab 'LiveTab'
    # Oberstes Programm auswählen, damit rechts die Details statt eines leeren Feldes zu sehen sind
    $rowCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $row = (Find 'AppList').FindFirst([System.Windows.Automation.TreeScope]::Children, $rowCond)
    if ($row) { $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    Start-Sleep 8; Shot 'live.png' # Graph etwas füllen lassen
    Select-Tab 'UsageTab'; Shot 'verbrauch.png'

    Select-Tab 'PingTab'
    Press 'PingButton'; Start-Sleep 40; Shot 'ping.png'
    Press 'PingButton'

    Select-Tab 'SpeedTab'
    Press 'SpeedButton'; Start-Sleep 30; Shot 'speedtest.png'

    Select-Tab 'LiveTab'
    Press $null 'Einstellungen'; Start-Sleep 1
    # Einstellungen merken sich die Scrollposition – für das Bild nach ganz oben
    $scrollable = New-Object System.Windows.Automation.PropertyCondition($A::IsScrollPatternAvailableProperty, $true)
    foreach ($s in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $scrollable)) {
        $sp = $s.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($sp.Current.VerticallyScrollable -and $s.Current.BoundingRectangle.Width -lt 700) { $sp.SetScrollPercent(-1, 0) }
    }
    Start-Sleep 1; Shot 'einstellungen.png'
    Press $null "Einstellungen schlie$([char]0xDF)en" # ß nicht direkt: PowerShell 5.1 liest die Datei ohne BOM als ANSI
    'fertig'
}
catch { "FEHLER: $_" }
finally { Stop-Transcript | Out-Null }
