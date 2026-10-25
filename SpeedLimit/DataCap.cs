namespace SpeedLimit;

/// <summary>Datenlimit eines Programms: so viele Bytes pro Tag bzw. Monat, danach melden (und optional sperren).</summary>
public readonly record struct DataCap(long Bytes, bool Monthly, bool Block)
{
    public const double WarnAt = 0.8;

    /// <summary>Erster Tag des aktuellen Zeitraums.</summary>
    public DateOnly PeriodStart(DateOnly today) => Monthly ? new DateOnly(today.Year, today.Month, 1) : today;

    /// <summary>Kennung des Zeitraums, damit pro Tag/Monat nur einmal gemeldet wird.</summary>
    public string PeriodKey(DateOnly today) => Monthly ? $"{today:yyyy-MM}" : $"{today:yyyy-MM-dd}";

    public string PeriodText => Monthly ? "diesen Monat" : "heute";
}
