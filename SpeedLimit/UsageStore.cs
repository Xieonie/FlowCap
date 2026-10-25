using System.Globalization;
using System.IO;

namespace SpeedLimit;

/// <summary>Datenverbrauch je Tag und Programm in Bytes, gespeichert in usage.json.</summary>
public sealed class UsageStore
{
    public const int KeepDays = 90;
    static readonly string FilePath = Path.Combine(JsonFile.Dir, "usage.json");

    /// <summary>"yyyy-MM-dd" → Programm → [Download, Upload] in Bytes.</summary>
    public Dictionary<string, Dictionary<string, long[]>> Days { get; set; } = [];

    public void Add(DateOnly day, string app, long down, long up)
    {
        if (down == 0 && up == 0) return;
        var key = Key(day);
        if (!Days.TryGetValue(key, out var apps)) Days[key] = apps = [];
        if (!apps.TryGetValue(app, out var v)) apps[app] = v = new long[2];
        v[0] += down;
        v[1] += up;
    }

    /// <summary>Summe je Programm von <paramref name="from"/> bis einschließlich <paramref name="to"/>.</summary>
    public Dictionary<string, (long Down, long Up)> Totals(DateOnly from, DateOnly to)
    {
        var result = new Dictionary<string, (long Down, long Up)>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (!Days.TryGetValue(Key(d), out var apps)) continue;
            foreach (var (app, v) in apps)
            {
                var t = result.GetValueOrDefault(app);
                result[app] = (t.Down + v[0], t.Up + v[1]);
            }
        }
        return result;
    }

    /// <summary>Tage vor <paramref name="keepFrom"/> löschen.</summary>
    public void Prune(DateOnly keepFrom)
    {
        foreach (var key in Days.Keys.Where(k => DateOnly.ParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture) < keepFrom).ToList())
            Days.Remove(key);
    }

    static string Key(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static UsageStore Load() => JsonFile.Load<UsageStore>(FilePath);

    public void Save()
    {
        Prune(DateOnly.FromDateTime(DateTime.Today).AddDays(-KeepDays));
        JsonFile.Save(FilePath, this);
    }
}
