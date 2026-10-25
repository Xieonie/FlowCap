using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SpeedLimit;

/// <summary>Benannter Satz von Limits: pro Programm plus Gesamtlimit.</summary>
public sealed class Profile
{
    public Dictionary<string, Limit> Limits { get; set; } = [];
    public Limit TotalLimit { get; set; }
}

/// <summary>Zeitplan-Regel: an diesen Wochentagen von–bis gilt dieses Profil. Bindbar für die Einstellungsseite.</summary>
public sealed class ScheduleRule : INotifyPropertyChanged
{
    static readonly string[] TimeFormats = ["H:mm", "HH:mm"];
    static readonly string[] DayNames = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];

    bool enabled = true;
    string profile = "";
    int days = 0b0011111; // Bit 0 = Montag … Bit 6 = Sonntag; Standard Mo–Fr
    string from = "09:00", to = "17:00";

    public bool Enabled { get => enabled; set { enabled = value; Notify(); } }
    public string Profile { get => profile; set { profile = value ?? ""; Notify(); } }
    public int Days { get => days; set { days = value; Notify(); Notify(nameof(IsValid)); } }
    public string From { get => from; set { from = value; Notify(); Notify(nameof(IsValid)); } }
    public string To { get => to; set { to = value; Notify(); Notify(nameof(IsValid)); } }

    [JsonIgnore] public bool Mo { get => Day(0); set => SetDay(0, value); }
    [JsonIgnore] public bool Di { get => Day(1); set => SetDay(1, value); }
    [JsonIgnore] public bool Mi { get => Day(2); set => SetDay(2, value); }
    [JsonIgnore] public bool Do { get => Day(3); set => SetDay(3, value); }
    [JsonIgnore] public bool Fr { get => Day(4); set => SetDay(4, value); }
    [JsonIgnore] public bool Sa { get => Day(5); set => SetDay(5, value); }
    [JsonIgnore] public bool So { get => Day(6); set => SetDay(6, value); }

    /// <summary>Uhrzeiten lesbar und mindestens ein Tag gewählt.</summary>
    [JsonIgnore] public bool IsValid => TryTime(from, out _) && TryTime(to, out _) && days != 0;

    bool Day(int i) => (days & (1 << i)) != 0;
    void SetDay(int i, bool on)
    {
        Days = on ? days | (1 << i) : days & ~(1 << i);
        Notify(DayNames[i]);
    }

    static int Index(DayOfWeek d) => ((int)d + 6) % 7; // Montag = 0

    static bool TryTime(string s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    /// <summary>Gilt die Regel zu diesem Zeitpunkt? Von = Bis bedeutet ganzer Tag; Von > Bis läuft über Mitternacht.</summary>
    public bool Matches(DateTime now)
    {
        if (!enabled || !TryTime(from, out var f) || !TryTime(to, out var t)) return false;
        var time = TimeOnly.FromDateTime(now);
        var today = Day(Index(now.DayOfWeek));
        if (f == t) return today;
        if (f < t) return today && time >= f && time < t;
        var yesterday = Day(Index(now.AddDays(-1).DayOfWeek));
        return (today && time >= f) || (yesterday && time < t);
    }

    /// <summary>Erste passende Regel (Reihenfolge in der Liste = Priorität).</summary>
    public static ScheduleRule? ActiveRule(IEnumerable<ScheduleRule> rules, DateTime now) => rules.FirstOrDefault(r => r.Matches(now));

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? prop = null) => PropertyChanged?.Invoke(this, new(prop));
}
