namespace SpeedLimit;

/// <summary>
/// Erkennt auffällig viel Upload: lädt ein Programm innerhalb von <see cref="Window"/> mehr als die Schwelle hoch,
/// wird gemeldet – höchstens einmal pro Programm und Tag.
/// </summary>
public sealed class UploadWatch
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    readonly Dictionary<string, Queue<(DateTime Time, long Bytes)>> recent = [];
    readonly Dictionary<string, long> sums = [];
    readonly Dictionary<string, DateOnly> alerted = [];

    /// <summary>Upload eines Programms seit dem letzten Aufruf. Liefert die Bytes im Zeitfenster, wenn gewarnt werden soll, sonst null.</summary>
    public long? Add(string app, long bytes, DateTime now, long thresholdBytes)
    {
        if (!recent.TryGetValue(app, out var q)) recent[app] = q = new();
        if (bytes > 0) q.Enqueue((now, bytes));
        var sum = sums.GetValueOrDefault(app) + bytes;
        while (q.Count > 0 && now - q.Peek().Time > Window) sum -= q.Dequeue().Bytes;
        sums[app] = sum;

        var today = DateOnly.FromDateTime(now);
        if (sum < thresholdBytes || alerted.GetValueOrDefault(app) == today) return null;
        alerted[app] = today;
        return sum;
    }
}
