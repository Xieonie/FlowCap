using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;

namespace SpeedLimit;

/// <summary>Limit in KB/s, 0 = unbegrenzt; Blocked = gar kein Internet.</summary>
public readonly record struct Limit(int DownKBs, int UpKBs, bool Blocked = false);

public sealed class TokenBucket(double rate, double now)
{
    // Mindestens 64 KB, damit auch große (LSO-)Pakete jemals durchpassen
    readonly double capacity = Math.Max(rate * 0.2, 64 * 1024);
    double tokens = Math.Max(rate * 0.2, 64 * 1024);
    double last = now;

    public double Rate { get; } = rate;

    /// <summary>Füllt nach und prüft, ohne zu verbrauchen – nötig, wenn ein Paket zwei Buckets (Programm + gesamt) passieren muss.</summary>
    public bool CanTake(int bytes, double now)
    {
        tokens = Math.Min(capacity, tokens + (now - last) * Rate);
        last = now;
        return tokens >= bytes;
    }

    public void Take(int bytes) => tokens -= bytes;

    public bool TryTake(int bytes, double now)
    {
        if (!CanTake(bytes, now)) return false;
        Take(bytes);
        return true;
    }
}

/// <summary>
/// Fängt alle TCP/UDP-Pakete per WinDivert ab, ordnet sie über den lokalen Port einem Programm zu
/// und verzögert sie bei gesetztem Limit (Token-Bucket). Läuft der Puffer über, wird verworfen – TCP bremst dann selbst.
/// </summary>
public sealed class Throttler : IDisposable
{
    const string Unknown = ""; // Pakete ohne zuordenbares Programm (nur fürs Gesamtlimit relevant)

    readonly IntPtr handle;
    readonly object gate = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, Limit> limits;
    readonly Dictionary<(string Name, bool Up), TokenBucket> buckets = [];
    readonly TokenBucket?[] totalBuckets = new TokenBucket?[2];
    readonly TokenBucket?[] gameBuckets = new TokenBucket?[2];
    // Proxy pro Programm: Relay erst beim ersten zugewiesenen Proxy starten; neue Verbindungen mit noch unbekanntem Programm kurz zurückhalten
    const double HoldSeconds = 0.05;
    readonly List<(byte[] Data, WinDivertAddress Addr, double Until)> held = [];
    readonly string self = Process.GetCurrentProcess().ProcessName;
    ProxyRelay? relay;
    // VPN pro Programm: Programm → Tunnel; NAT merkt sich je lokalem Port die echte Absenderadresse und eine Paket-Adresse fürs Zurückspielen
    Dictionary<string, WireGuardTunnel> vpnApps = [];
    readonly Dictionary<(bool Tcp, ushort Port), (byte[] LocalIp, WinDivertAddress Addr, string App)> vpnNat = [];
    readonly HashSet<WireGuardTunnel> subscribed = [];
    HashSet<string> capBlocked = [];
    HashSet<string>? priority; // null = Spielmodus nicht aktiv
    Limit gameLimit;
    readonly Dictionary<(string Name, bool Up), Queue<(byte[] Data, WinDivertAddress Addr)>> queues = [];
    readonly Dictionary<(string Name, bool Up), long> queuedBytes = [];
    Dictionary<(string Name, bool Up), long> traffic = [];
    Limit total;
    volatile bool stopping, paused;

    public PortMap Ports { get; } = new();

    /// <summary>Alle Limits und Sperren vorübergehend aussetzen.</summary>
    public bool Paused { get => paused; set => paused = value; }

    public Throttler(IReadOnlyDictionary<string, Limit> initialLimits, Limit totalLimit)
    {
        limits = new(initialLimits);
        total = totalLimit;
        handle = WinDivert.WinDivertOpen("(tcp or udp) and not loopback", 0, 0, 0);
        if (handle == new IntPtr(-1))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            var reason = err switch
            {
                5 => "Keine Administratorrechte.",
                2 => "WinDivert64.sys wurde nicht gefunden.",
                577 => "Windows hat die Treibersignatur abgelehnt.",
                1275 => "Der Treiber wurde von Windows blockiert (Virenschutz oder Speicherintegrität).",
                _ => new Win32Exception(err).Message,
            };
            throw new InvalidOperationException($"Der Netzwerktreiber konnte nicht gestartet werden:\n{reason} (Fehler {err})");
        }
        new Thread(Capture) { IsBackground = true, Name = "Capture", Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(Release) { IsBackground = true, Name = "Release", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void SetLimit(string name, Limit limit)
    {
        lock (gate)
        {
            if (limit == default) limits.Remove(name);
            else limits[name] = limit;
        }
    }

    /// <summary>Alle Programm-Limits auf einmal ersetzen (Profilwechsel).</summary>
    public void ReplaceLimits(IReadOnlyDictionary<string, Limit> all)
    {
        lock (gate)
        {
            limits.Clear();
            foreach (var (name, limit) in all) limits[name] = limit;
        }
    }

    /// <summary>Limit für alle Programme zusammen (Blocked wird ignoriert).</summary>
    public void SetTotalLimit(Limit limit)
    {
        lock (gate) total = limit;
    }

    /// <summary>Programme, deren Datenlimit erreicht ist und die deshalb gesperrt sind (zusätzlich zu manuellen Sperren).</summary>
    public void SetCapBlocked(IEnumerable<string> names)
    {
        var set = names.ToHashSet();
        lock (gate) capBlocked = set;
    }

    /// <summary>Spielmodus: alle Programme außer <paramref name="priorityApps"/> teilen sich <paramref name="limit"/>; null = aus.</summary>
    public void SetGameMode(IEnumerable<string>? priorityApps, Limit limit)
    {
        var set = priorityApps?.ToHashSet();
        lock (gate)
        {
            priority = set;
            gameLimit = limit;
        }
    }

    /// <summary>Programm → Proxy. Startet beim ersten Eintrag das lokale Relay.</summary>
    public void SetProxies(Dictionary<string, ProxyServer> map)
    {
        if (map.Count > 0) EnsureRelay();
        relay?.SetProxies(map);
    }

    /// <summary>Programm → WireGuard-Tunnel. Startet bei Bedarf den Socket-Beobachter (über das Relay), damit neue Verbindungen sofort zugeordnet werden.</summary>
    public void SetVpns(Dictionary<string, WireGuardTunnel> map)
    {
        if (map.Count > 0) EnsureRelay();
        lock (gate)
        {
            vpnApps = map;
            foreach (var t in map.Values.Where(subscribed.Add)) t.PacketReceived += OnTunnelPacket;
        }
    }

    void EnsureRelay()
    {
        if (relay is not null) return;
        var r = new ProxyRelay(Ports); // außerhalb des Locks: legt u. a. eine Firewall-Regel an (dauert)
        lock (gate) relay = r;
    }

    /// <summary>Antwort aus dem Tunnel an das Programm zurückgeben (Zieladresse zurück auf die echte lokale Adresse).</summary>
    void OnTunnelPacket(WireGuardTunnel tunnel, byte[] p)
    {
        if (!PacketInfo.TryParse(p, out var i) || !i.V4 || !p.AsSpan(16, 4).SequenceEqual(tunnel.Address.GetAddressBytes())) return;
        lock (gate)
        {
            // Nur Antworten auf Verbindungen, die ein zugewiesenes Programm selbst aufgebaut hat
            if (!vpnNat.TryGetValue((i.Tcp, i.DstPort), out var nat) || vpnApps.GetValueOrDefault(nat.App) != tunnel) return;
            if (IsBlocked(nat.App)) return;
            PacketInfo.SetDestination(p, nat.LocalIp);
            if (i.Tcp) PacketInfo.ClampMss(p, tunnel.Mtu - 40);
            var addr = nat.Addr;
            addr.Outbound = false;
            WinDivert.WinDivertHelperCalcChecksums(p, (uint)p.Length, ref addr, 0);
            Count((nat.App, false), (uint)p.Length);
            WinDivert.WinDivertSend(handle, p, (uint)p.Length, out _, addr);
        }
    }

    /// <summary>Paket eines VPN-Programms in den Tunnel schicken. Ist der Tunnel getrennt, wird verworfen – nichts geht am VPN vorbei.</summary>
    void SendToTunnel(WireGuardTunnel tunnel, string app, PacketInfo info, byte[] buf, uint len, WinDivertAddress addr)
    {
        if (!info.V4 || IsBlocked(app)) return; // IPv6 kann der Tunnel nicht → sperren statt durchlassen
        var p = buf[..(int)len];
        vpnNat[(info.Tcp, info.SrcPort)] = (p[12..16], addr, app);
        if (info.Tcp) PacketInfo.ClampMss(p, tunnel.Mtu - 40); // 40 = IPv4- + TCP-Kopf
        PacketInfo.SetSource(p, tunnel.Address.GetAddressBytes());
        PacketInfo.FixChecksums(p);
        Count((app, true), len);
        // shortcut: Bandbreiten-Limits gelten nicht für VPN-Verkehr (nur Sperre und Zählung), bei Bedarf über Throttle() leiten
        if (tunnel.State == TunnelState.Down) return;
        foreach (var segment in PacketInfo.Segment(p, tunnel.Mtu)) tunnel.Send(segment);
    }

    /// <summary>Letzter Fehler beim Verbinden über einen Proxy.</summary>
    public string? ProxyError => relay?.LastError;

    /// <summary>Bytes pro (Programm, Richtung) seit dem letzten Aufruf.</summary>
    public Dictionary<(string Name, bool Up), long> TakeTraffic()
    {
        lock (gate)
        {
            var t = traffic;
            traffic = [];
            return t;
        }
    }

    /// <summary>Lokaler Port aus einem IPv4/IPv6-Paket (bei ausgehenden Paketen Quell-, sonst Zielport).</summary>
    public static bool TryGetLocalPort(ReadOnlySpan<byte> p, bool outbound, out bool tcp, out ushort port)
    {
        var ok = PacketInfo.TryParse(p, out var info);
        tcp = info.Tcp;
        port = outbound ? info.SrcPort : info.DstPort;
        return ok;
    }

    void Capture()
    {
        var buf = new byte[0xFFFF];
        while (!stopping && WinDivert.WinDivertRecv(handle, buf, (uint)buf.Length, out var len, out var addr))
            lock (gate) Route(buf, len, addr, canHold: true);
    }

    /// <summary>Ein Paket einordnen: ggf. zum Proxy-Relay spiegeln, sperren, drosseln oder sofort senden. Läuft unter <see cref="gate"/>.</summary>
    void Route(byte[] buf, uint len, WinDivertAddress addr, bool canHold)
    {
        var up = addr.Outbound;
        var pkt = buf.AsSpan(0, (int)len);
        var parsed = PacketInfo.TryParse(pkt, out var info);
        var name = parsed ? Ports.Lookup(info.Tcp, up ? info.SrcPort : info.DstPort) : null;

        if (relay is { } r && parsed && up)
        {
            if (info.Tcp && info.V4 && info.SrcPort == r.Port)
            {
                // Antwort des Relays zurück an das Programm – zählt als dessen Download
                if (!r.TryGetNat(info.DstPort, out var nat)) return;
                info.ReflectFromRelay(pkt, nat.RemotePort);
                addr.Outbound = false;
                WinDivert.WinDivertHelperCalcChecksums(buf, len, ref addr, 0);
                name = nat.App;
                up = false;
            }
            else
            {
                name = r.Lookup(info.Tcp, info.SrcPort) ?? name;
                // Neue Verbindung, deren Programm noch unbekannt ist: kurz auf das Socket-Ereignis warten, sonst ginge sie am Proxy vorbei
                if (name is null && canHold && (info.Syn || info.Udp))
                {
                    held.Add((buf[..(int)len], addr, clock.Elapsed.TotalSeconds + HoldSeconds));
                    return;
                }
                if (name is not null && vpnApps.TryGetValue(name, out var tunnel))
                {
                    SendToTunnel(tunnel, name, info, buf, len, addr);
                    return;
                }
                if (r.ProxyFor(name) is { } proxy)
                {
                    if (!info.Tcp || !info.V4) return; // UDP (z. B. QUIC) und IPv6 würden am Proxy vorbeigehen → sperren
                    if (info.Syn) r.Register(info.SrcPort, pkt[16..20].ToArray(), info.DstPort, name!, proxy);
                    // shortcut: Verbindungen von vor dem Zuweisen des Proxys laufen direkt weiter, bis sie enden
                    if (r.TryGetNat(info.SrcPort, out _))
                    {
                        info.ReflectToRelay(pkt, r.Port);
                        addr.Outbound = false;
                        WinDivert.WinDivertHelperCalcChecksums(buf, len, ref addr, 0);
                    }
                }
            }
        }

        var key = (name ?? Unknown, up);
        if (IsBlocked(key.Item1)) return; // gesperrt: verwerfen
        Throttle(key, buf, len, addr);
    }

    /// <summary>Drosseln oder sofort senden. Läuft unter <see cref="gate"/>.</summary>
    void Throttle((string Name, bool Up) key, byte[] buf, uint len, WinDivertAddress addr)
    {
        var b = BucketsFor(key);
        var pending = queues.TryGetValue(key, out var q) && q.Count > 0;
        if (b.None && !pending)
        {
            Count(key, len);
            WinDivert.WinDivertSend(handle, buf, len, out _, addr);
            return;
        }
        // Puffer max. ~0,5 s – darüber verwerfen, sonst steigt nur die Latenz
        if (queuedBytes.GetValueOrDefault(key) > Math.Max(b.MinRate * 0.5, 64 * 1024)) return;
        if (q is null) queues[key] = q = new();
        q.Enqueue((buf[..(int)len], addr));
        queuedBytes[key] = queuedBytes.GetValueOrDefault(key) + len;
    }

    /// <summary>Zurückgehaltene Pakete weiterleiten, sobald ihr Programm bekannt ist (oder die Wartezeit um ist).</summary>
    void ReleaseHeld(double now)
    {
        if (held.Count == 0) return;
        var ready = held.Where(h => now >= h.Until || relay is null || IsKnown(h.Data, h.Addr.Outbound)).ToList();
        if (ready.Count == 0) return;
        held.RemoveAll(ready.Contains);
        foreach (var (data, addr, _) in ready) Route(data, (uint)data.Length, addr, canHold: false);
    }

    bool IsKnown(byte[] data, bool up) =>
        PacketInfo.TryParse(data, out var i) && (relay?.Lookup(i.Tcp, up ? i.SrcPort : i.DstPort) ?? Ports.Lookup(i.Tcp, up ? i.SrcPort : i.DstPort)) is not null;

    void Release()
    {
        while (!stopping)
        {
            var now = clock.Elapsed.TotalSeconds;
            lock (gate)
            {
                ReleaseHeld(now);
                // Reihum je ein Paket pro Warteschlange, damit unter dem Gesamtlimit kein Programm die anderen aushungert
                bool progress;
                do
                {
                    progress = false;
                    foreach (var (key, q) in queues)
                    {
                        if (q.Count == 0) continue;
                        var size = q.Peek().Data.Length;
                        var b = BucketsFor(key);
                        if (!b.CanTake(size, now)) continue;
                        b.Take(size);
                        var (data, addr) = q.Dequeue();
                        queuedBytes[key] -= data.Length;
                        Count(key, (uint)data.Length); // nur wirklich Durchgelassenes zählen
                        WinDivert.WinDivertSend(handle, data, (uint)data.Length, out _, addr);
                        progress = true;
                    }
                } while (progress);
            }
            Thread.Sleep(5);
        }
    }

    void Count((string Name, bool Up) key, uint bytes)
    {
        // FlowCap selbst nicht zählen: sein Verkehr ist nur der Proxy-Weg der umgeleiteten Programme, die schon gezählt werden
        if (key.Name != Unknown && key.Name != self) traffic[key] = traffic.GetValueOrDefault(key) + bytes;
    }

    bool IsBlocked(string name) => !paused && ((limits.TryGetValue(name, out var l) && l.Blocked) || capBlocked.Contains(name));

    /// <summary>Die Buckets, die ein Paket passieren muss: Programm-Limit, Gesamtlimit, Spielmodus.</summary>
    readonly record struct Gates(TokenBucket? App, TokenBucket? All, TokenBucket? Game)
    {
        public bool None => App is null && All is null && Game is null;
        public double MinRate => Math.Min(App?.Rate ?? double.MaxValue, Math.Min(All?.Rate ?? double.MaxValue, Game?.Rate ?? double.MaxValue));
        public bool CanTake(int size, double now) =>
            (App?.CanTake(size, now) ?? true) && (All?.CanTake(size, now) ?? true) && (Game?.CanTake(size, now) ?? true);
        public void Take(int size)
        {
            App?.Take(size);
            All?.Take(size);
            Game?.Take(size);
        }
    }

    // FlowCap selbst (Speedtest, Proxy-Relay) läuft nie unter Gesamtlimit/Spielmodus: der Speedtest soll die echte Leitung messen,
    // und Proxy-Verkehr ist schon beim umgeleiteten Programm gedrosselt
    Gates BucketsFor((string Name, bool Up) key) =>
        key.Name == self ? default : new(BucketFor(key), TotalBucket(key.Up), GameBucket(key));

    TokenBucket? GameBucket((string Name, bool Up) key)
    {
        if (paused || priority is null || priority.Contains(key.Name)) return null;
        var i = key.Up ? 1 : 0;
        return gameBuckets[i] = Bucket(gameBuckets[i], key.Up ? gameLimit.UpKBs : gameLimit.DownKBs);
    }

    TokenBucket? BucketFor((string Name, bool Up) key)
    {
        if (paused || !limits.TryGetValue(key.Name, out var l)) return null;
        var b = Bucket(buckets.GetValueOrDefault(key), key.Up ? l.UpKBs : l.DownKBs);
        if (b is not null) buckets[key] = b;
        return b;
    }

    TokenBucket? TotalBucket(bool up)
    {
        if (paused) return null;
        var i = up ? 1 : 0;
        return totalBuckets[i] = Bucket(totalBuckets[i], up ? total.UpKBs : total.DownKBs);
    }

    // Bestehenden Bucket behalten, solange sich die Rate nicht ändert (sonst gingen angesparte Tokens verloren)
    TokenBucket? Bucket(TokenBucket? existing, int kbs)
    {
        if (kbs <= 0) return null;
        var rate = kbs * 1024.0;
        return existing?.Rate == rate ? existing : new TokenBucket(rate, clock.Elapsed.TotalSeconds);
    }

    public void Dispose()
    {
        stopping = true;
        relay?.Dispose();
        WinDivert.WinDivertShutdown(handle, 3); // RECV + SEND, beendet den blockierenden Recv
        WinDivert.WinDivertClose(handle);      // ab hier fließt der Verkehr wieder ungefiltert
    }
}
