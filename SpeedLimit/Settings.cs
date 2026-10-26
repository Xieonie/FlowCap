using System.IO;
using System.Text.Json;

namespace SpeedLimit;

public sealed class Settings
{
    static readonly string FilePath = Path.Combine(JsonFile.Dir, "settings.json");
    static readonly string LegacyPath = Path.Combine(JsonFile.AppData, "SpeedLimit", "settings.json"); // Name bis Version 1.0

    public Dictionary<string, Limit> Limits { get; set; } = [];
    /// <summary>Limit für alle Programme zusammen.</summary>
    public Limit TotalLimit { get; set; }
    /// <summary>Anordnung der Module in der Live-Ansicht.</summary>
    public DashboardLayout Layout { get; set; } = new();
    /// <summary>Id des Farbschemas (siehe <see cref="SpeedLimit.Theme.All"/>).</summary>
    public string Theme { get; set; } = "graphit";
    /// <summary>Raten in Mbit/s statt KB/s anzeigen.</summary>
    public bool UseBits { get; set; }
    /// <summary>Zeitraum des großen Graphen: 60, 300 oder 900 Sekunden.</summary>
    public int GraphSeconds { get; set; } = 60;
    /// <summary>Beim manuellen Start nur das Tray-Symbol zeigen.</summary>
    public bool StartInTray { get; set; }

    /// <summary>Datenlimits je Programm.</summary>
    public Dictionary<string, DataCap> DataCaps { get; set; } = [];
    /// <summary>Melden, wenn ein Programm zum ersten Mal Daten sendet.</summary>
    public bool NotifyNewApps { get; set; } = true;
    /// <summary>Programme, die schon einmal im Internet waren; null = noch nie erfasst (erste Erfassung ohne Meldungen).</summary>
    public HashSet<string>? KnownApps { get; set; }

    /// <summary>Spielmodus: läuft ein Programm mit Vorrang, werden alle anderen zusammen gedrosselt.</summary>
    public bool GameModeEnabled { get; set; }
    public HashSet<string> PriorityApps { get; set; } = [];
    /// <summary>Gemeinsames Limit aller übrigen Programme im Spielmodus.</summary>
    public Limit GameModeLimit { get; set; } = new(1024, 512);

    /// <summary>Gespeicherte Profile. Limits/TotalLimit oben sind die Live-Werte des Profils <see cref="ActiveProfile"/>.</summary>
    public Dictionary<string, Profile> Profiles { get; set; } = [];
    /// <summary>Profil, zu dem die Live-Werte gehören.</summary>
    public string ActiveProfile { get; set; } = "Standard";
    /// <summary>Manuell gewähltes Profil – gilt, solange kein Zeitplan greift.</summary>
    public string SelectedProfile { get; set; } = "Standard";
    public System.Collections.ObjectModel.ObservableCollection<ScheduleRule> Schedules { get; set; } = [];

    /// <summary>Eingerichtete Proxy-Server.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProxyServer> Proxies { get; set; } = [];
    /// <summary>Programm → Name des Proxys (gilt profilübergreifend).</summary>
    public Dictionary<string, string> AppProxies { get; set; } = [];

    /// <summary>Geladene WireGuard-Configs.</summary>
    public System.Collections.ObjectModel.ObservableCollection<VpnEntry> Vpns { get; set; } = [];
    /// <summary>Programm → Name des VPN (hat Vorrang vor einem Proxy).</summary>
    public Dictionary<string, string> AppVpns { get; set; } = [];

    /// <summary>Letzte Speedtests, neuester zuerst.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SpeedResult> SpeedTests { get; set; } = [];
    public string PingTarget { get; set; } = "Cloudflare (1.1.1.1)";

    /// <summary>Live-Werte als Profil-Kopie.</summary>
    public Profile Snapshot() => new() { Limits = new(Limits), TotalLimit = TotalLimit };

    /// <summary>Sorgt dafür, dass aktives und gewähltes Profil existieren (auch bei alten Einstellungsdateien ohne Profile).</summary>
    public void EnsureProfiles()
    {
        if (string.IsNullOrWhiteSpace(ActiveProfile)) ActiveProfile = "Standard";
        Profiles[ActiveProfile] = Snapshot();
        if (!Profiles.ContainsKey(SelectedProfile)) SelectedProfile = ActiveProfile;
    }

    public static Settings Load()
    {
        if (!File.Exists(FilePath) && File.Exists(LegacyPath))
        {
            Directory.CreateDirectory(JsonFile.Dir);
            File.Copy(LegacyPath, FilePath);
        }
        return JsonFile.Load<Settings>(FilePath);
    }

    public void Save() => JsonFile.Save(FilePath, this);
}

static class JsonFile
{
    public static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static readonly string Dir = Path.Combine(AppData, "FlowCap");

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bak", overwrite: true); // kaputte Datei nicht still überschreiben
            return new();
        }
    }

    public static void Save(string path, object value)
    {
        Directory.CreateDirectory(Dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
