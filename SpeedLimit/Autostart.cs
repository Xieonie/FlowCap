using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace SpeedLimit;

/// <summary>
/// Autostart über die Aufgabenplanung statt Run-Registry: nur so startet ein Admin-Programm bei der Anmeldung ohne UAC-Abfrage.
/// </summary>
static class Autostart
{
    const string TaskName = "FlowCap";

    public static bool IsEnabled => Schtasks("/Query", "/TN", TaskName) == 0;

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            Schtasks("/Delete", "/TN", TaskName, "/F");
            return;
        }
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(Environment.ProcessPath!);
        // Per XML, weil schtasks /Create sonst nach 72 h beendet und im Akkubetrieb nicht startet
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "FlowCap-task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            if (Schtasks("/Create", "/TN", TaskName, "/XML", tmp, "/F") != 0)
                throw new InvalidOperationException("Die Autostart-Aufgabe konnte nicht angelegt werden.");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
