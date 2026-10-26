using System.IO;

namespace SpeedLimit;

/// <summary>5-Minuten-Zusammenfassung der Hintergrund-Pings: gesendet, verloren, Mittel und Maximum der Antworten.</summary>
public sealed record PingBucket(DateTime Start, int Sent, int Lost, double? Avg, double? Max)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;

    /// <summary>Mehrere Abschnitte zu einem zusammenfassen (für 7/30 Tage); Mittel gewichtet nach Antworten.</summary>
    public static PingBucket Merge(DateTime start, IReadOnlyCollection<PingBucket> parts)
    {
        var ok = parts.Sum(p => p.Sent - p.Lost);
        var avg = ok == 0 ? (double?)null : parts.Where(p => p.Avg is not null).Sum(p => p.Avg!.Value * (p.Sent - p.Lost)) / ok;
        return new(start, parts.Sum(p => p.Sent), parts.Sum(p => p.Lost), avg, parts.Max(p => p.Max));
    }
}

/// <summary>Ping-Verlauf über Tage (aus der Ping-Anzeige alle 5 s), gespeichert in ping.json.</summary>
public sealed class PingLog
{
    public const int KeepDays = 30;
    public static readonly TimeSpan BucketSize = TimeSpan.FromMinutes(5);
    static readonly string FilePath = Path.Combine(JsonFile.Dir, "ping.json");

    public List<PingBucket> Buckets { get; set; } = [];

    /// <summary>Ein Ping-Ergebnis eintragen; null = keine Antwort.</summary>
    public void Add(DateTime now, double? ms)
    {
        var start = new DateTime(now.Ticks - now.Ticks % BucketSize.Ticks, now.Kind);
        if (Buckets.Count == 0 || Buckets[^1].Start != start) Buckets.Add(new(start, 0, 0, null, null));
        var b = Buckets[^1];
        var ok = b.Sent - b.Lost;
        Buckets[^1] = ms is { } v
            ? b with { Sent = b.Sent + 1, Avg = ((b.Avg ?? 0) * ok + v) / (ok + 1), Max = Math.Max(b.Max ?? v, v) }
            : b with { Sent = b.Sent + 1, Lost = b.Lost + 1 };
    }

    /// <summary>
    /// Zeitraum [from, from + count·step) in gleich lange Abschnitte teilen. null = keine Messung (PC aus, FlowCap beendet) –
    /// das ist etwas anderes als 0 ms und wird im Graphen als Lücke gezeigt.
    /// </summary>
    public PingBucket?[] Range(DateTime from, TimeSpan step, int count)
    {
        var result = new PingBucket?[count];
        foreach (var group in Buckets.Where(b => b.Start >= from && b.Start < from + step * count)
                     .GroupBy(b => (int)((b.Start - from).Ticks / step.Ticks)))
            result[group.Key] = PingBucket.Merge(from + step * group.Key, group.ToList());
        return result;
    }

    public static PingLog Load() => JsonFile.Load<PingLog>(FilePath);

    public void Save()
    {
        Buckets.RemoveAll(b => b.Start < DateTime.Now.AddDays(-KeepDays));
        JsonFile.Save(FilePath, this);
    }
}
