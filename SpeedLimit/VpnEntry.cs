using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SpeedLimit;

/// <summary>Eine geladene WireGuard-Config. Der Text (inkl. privatem Schlüssel) liegt nur DPAPI-verschlüsselt in den Einstellungen.</summary>
public sealed class VpnEntry : INotifyPropertyChanged
{
    bool killSwitch = true;
    string status = "Keinem Programm zugewiesen";

    public string Name { get; set; } = "";
    public string ProtectedConfig { get; set; } = "";

    /// <summary>Verliert der Tunnel die Verbindung (oder wird FlowCap beendet), die zugewiesenen Programme beenden.</summary>
    public bool KillSwitch { get => killSwitch; set { killSwitch = value; Notify(); } }

    [JsonIgnore] public string Status { get => status; set { if (status != value) { status = value; Notify(); } } }

    /// <summary>Prüft die Config und legt sie verschlüsselt ab; wirft <see cref="FormatException"/> bei ungültiger Config.</summary>
    public static VpnEntry Create(string name, string configText)
    {
        WireGuardConfig.Parse(configText);
        return new VpnEntry
        {
            Name = name,
            ProtectedConfig = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(configText), null, DataProtectionScope.CurrentUser)),
        };
    }

    public WireGuardConfig Config() =>
        WireGuardConfig.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedConfig), null, DataProtectionScope.CurrentUser)));

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? prop = null) => PropertyChanged?.Invoke(this, new(prop));
}
