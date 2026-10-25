using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace SpeedLimit;

/// <summary>Ergebnis eines Speedtests.</summary>
public sealed record SpeedResult(DateTime Time, double PingMs, double JitterMs, double DownMbps, double UpMbps);

/// <summary>Ping (ICMP) und Durchsatz über die öffentlichen Speedtest-Endpunkte von Cloudflare.</summary>
public static class SpeedTest
{
    public const string PingHost = "1.1.1.1";
    // 50 MB ist das Maximum, das Cloudflare pro Anfrage liefert; größere Stücke = weniger Anfragen = seltener "429 Too Many Requests"
    const string DownUrl = "https://speed.cloudflare.com/__down?bytes=50000000";
    const string UpUrl = "https://speed.cloudflare.com/__up";
    const int Streams = 4;
    const long UploadChunk = 25_000_000;
    // Die ersten Sekunden ignorieren: TCP muss erst auf Tempo kommen, sonst fällt das Ergebnis zu niedrig aus
    static readonly TimeSpan Duration = TimeSpan.FromSeconds(10), WarmUp = TimeSpan.FromSeconds(2);

    static readonly HttpClient Http = new(new SocketsHttpHandler { MaxConnectionsPerServer = Streams * 2 }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Einzelner Ping in ms, null wenn keine Antwort.</summary>
    public static async Task<double?> PingOnceAsync(CancellationToken ct = default)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(IPAddress.Parse(PingHost), TimeSpan.FromSeconds(2), cancellationToken: ct);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    /// <summary>Mehrere Pings: Mittelwert und Jitter in ms.</summary>
    public static async Task<(double Ping, double Jitter)> PingAsync(int count, CancellationToken ct)
    {
        var times = new List<double>();
        for (var i = 0; i < count; i++)
        {
            if (await PingOnceAsync(ct) is { } ms) times.Add(ms);
            await Task.Delay(150, ct);
        }
        if (times.Count == 0) throw new IOException($"Keine Antwort von {PingHost}.");
        return (times.Average(), Jitter(times));
    }

    /// <summary>Jitter = mittlere Abweichung aufeinanderfolgender Pings.</summary>
    public static double Jitter(IReadOnlyList<double> times) =>
        times.Count < 2 ? 0 : times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average();

    public static double Mbps(long bytes, TimeSpan time) => time.TotalSeconds <= 0 ? 0 : bytes * 8 / time.TotalSeconds / 1_000_000;

    public static Task<double> DownloadAsync(IProgress<double> live, CancellationToken ct) => Measure(live, ct, async (count, token) =>
    {
        using var response = await Http.GetAsync(DownUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buf, token)) > 0) count(n);
    });

    public static Task<double> UploadAsync(IProgress<double> live, CancellationToken ct) => Measure(live, ct, async (count, token) =>
    {
        using var response = await Http.PostAsync(UpUrl, new CountingContent(UploadChunk, count), token);
        response.EnsureSuccessStatusCode();
    });

    /// <summary>Lässt <see cref="Streams"/> parallele Übertragungen <see cref="Duration"/> lang laufen und liefert Mbit/s ohne Anlaufphase.</summary>
    static async Task<double> Measure(IProgress<double> live, CancellationToken ct, Func<Action<int>, CancellationToken, Task> transfer)
    {
        long bytes = 0, bytesAtWarmUp = 0;
        var clock = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(Duration);
        void Count(int n) => Interlocked.Add(ref bytes, n);

        async Task Worker()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await transfer(Count, stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException && stop.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpRequestException e) when (Interlocked.Read(ref bytes) > 0)
                {
                    _ = e; // z. B. 429 mitten im Test: diese Verbindung beenden, die übrigen Daten reichen für das Ergebnis
                    return;
                }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new IOException("Cloudflare begrenzt gerade die Anzahl der Tests. Bitte in einer Minute erneut versuchen.", e);
                }
            }
        }

        var workers = Enumerable.Range(0, Streams).Select(_ => Worker()).ToList();
        var last = (Bytes: 0L, Time: TimeSpan.Zero);
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { break; }
            var now = (Bytes: Interlocked.Read(ref bytes), Time: clock.Elapsed);
            if (bytesAtWarmUp == 0 && now.Time >= WarmUp) bytesAtWarmUp = now.Bytes;
            live.Report(Mbps(now.Bytes - last.Bytes, now.Time - last.Time));
            last = now;
        }
        await Task.WhenAll(workers);
        ct.ThrowIfCancellationRequested();
        return Mbps(Interlocked.Read(ref bytes) - bytesAtWarmUp, clock.Elapsed - WarmUp);
    }

    /// <summary>Upload-Inhalt, der Nullbytes in Blöcken erzeugt und jeden gesendeten Block meldet.</summary>
    sealed class CountingContent(long length, Action<int> sent) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var block = new byte[64 * 1024];
            for (long left = length; left > 0; left -= block.Length)
            {
                var n = (int)Math.Min(block.Length, left);
                await stream.WriteAsync(block.AsMemory(0, n), ct);
                sent(n);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }
}
