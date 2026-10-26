using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using static SpeedLimit.WireGuardCrypto;

namespace SpeedLimit;

public enum TunnelState { Connecting, Connected, Down }

/// <summary>
/// WireGuard-Client im Programm (ohne Netzwerkadapter): verschlüsselt IP-Pakete und schickt sie per UDP an den Server,
/// entschlüsselt Antworten und meldet sie über <see cref="PacketReceived"/>. Protokoll nach dem WireGuard-Whitepaper.
/// </summary>
public sealed class WireGuardTunnel : IDisposable
{
    // Zeiten in Sekunden (Whitepaper 6.1); IdleCheck/DownAfter sind FlowCaps Verbindungsüberwachung für den Kill-Switch
    const double RekeyAfter = 120, RejectAfter = 180, RekeyTimeout = 5, PassiveKeepalive = 10, IdleCheck = 15, DownAfter = 25;
    const int MaxQueued = 256;

    sealed class Session(uint localIndex, uint remoteIndex, byte[] sendKey, byte[] recvKey, double created)
    {
        public uint LocalIndex = localIndex, RemoteIndex = remoteIndex;
        public byte[] SendKey = sendKey, RecvKey = recvKey;
        public double Created = created;
        public ulong SendCounter, RecvMax;
        public ulong RecvWindow; // Bit i = Zähler RecvMax - i schon gesehen
        public bool AnyReceived;
    }

    readonly WireGuardConfig cfg;
    readonly byte[] staticPub, mac1Key;
    readonly UdpClient udp = new(0, AddressFamily.InterNetwork);
    readonly object sync = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly CancellationTokenSource cts = new();
    readonly List<byte[]> queued = [];
    IPEndPoint? endpoint;
    double nextResolve;

    // laufender Handshake
    uint hsIndex;
    byte[]? hsChain, hsHash, hsEphemeral;
    double hsSentAt = -1;

    Session? current, previous;
    double lastReceived = -1, lastReceivedData = -1, lastSent = -1, lastHandshake = -1;

    public string Name { get; }
    public IPAddress Address => cfg.Address;
    /// <summary>IPv6-Adresse im Tunnel; null = Tunnel kann kein IPv6 (dann wird IPv6 der Programme gesperrt).</summary>
    public IPAddress? Address6 => cfg.Address6;
    /// <summary>DNS-Server des Anbieters aus der Config (DNS = …), für „DNS über den Tunnel“.</summary>
    public IPAddress? DnsServer => cfg.Dns;
    public int Mtu => cfg.Mtu;
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    /// <summary>Entschlüsseltes IP-Paket vom Server (wird außerhalb interner Locks aufgerufen).</summary>
    public event Action<WireGuardTunnel, byte[]>? PacketReceived;

    public WireGuardTunnel(string name, WireGuardConfig cfg)
    {
        Name = name;
        this.cfg = cfg;
        staticPub = PublicKey(cfg.PrivateKey);
        mac1Key = Hash(LabelMac1, cfg.PeerPublicKey);
        new Thread(ReceiveLoop) { IsBackground = true, Name = $"WG-Recv {name}" }.Start();
        new Thread(TimerLoop) { IsBackground = true, Name = $"WG-Timer {name}" }.Start();
    }

    double Now => clock.Elapsed.TotalSeconds;

    public TunnelState State
    {
        get
        {
            lock (sync)
            {
                var now = Now;
                if (current is not null && now - current.Created < RejectAfter && lastReceived >= 0 && now - lastReceived < DownAfter)
                    return TunnelState.Connected;
                return now - Math.Max(lastReceived, 0) < DownAfter ? TunnelState.Connecting : TunnelState.Down;
            }
        }
    }

    /// <summary>Sekunden seit dem letzten erfolgreichen Handshake, null wenn noch keiner.</summary>
    public double? SecondsSinceHandshake { get { lock (sync) return lastHandshake < 0 ? null : Now - lastHandshake; } }

    public string EndpointText => $"{cfg.EndpointHost}:{cfg.EndpointPort}";

    /// <summary>IP-Paket verschlüsselt an den Server schicken. Ohne gültige Sitzung wird es kurz gepuffert, bis der Handshake steht.</summary>
    public void Send(ReadOnlySpan<byte> packet)
    {
        lock (sync)
        {
            if (current is null || Now - current.Created >= RejectAfter)
            {
                if (queued.Count < MaxQueued) queued.Add(packet.ToArray());
                InitiateHandshake(force: false);
                return;
            }
            SendTransport(current, packet);
        }
    }

    void SendTransport(Session s, ReadOnlySpan<byte> packet)
    {
        if (endpoint is null) return;
        var padded = new byte[(packet.Length + 15) & ~15]; // Whitepaper: auf 16 Byte auffüllen
        packet.CopyTo(padded);
        var counter = s.SendCounter++;
        var cipher = Seal(s.SendKey, counter, padded, []);
        var msg = new byte[16 + cipher.Length];
        msg[0] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(4), s.RemoteIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(8), counter);
        cipher.CopyTo(msg, 16);
        Transmit(msg);
        BytesSent += packet.Length;
    }

    void Transmit(byte[] msg)
    {
        try
        {
            udp.Send(msg, msg.Length, endpoint);
            lastSent = Now;
        }
        catch (SocketException)
        {
            // Netz weg – die Überwachung meldet "getrennt"
        }
        catch (ObjectDisposedException)
        {
            // Tunnel wurde gerade entfernt, der Throttler kennt ihn aber noch einen Moment
        }
    }

    // Handshake-Initiierung (Whitepaper 5.4.2)
    void InitiateHandshake(bool force)
    {
        var now = Now;
        if (!force && hsSentAt >= 0 && now - hsSentAt < RekeyTimeout) return; // läuft schon
        if (!ResolveEndpoint(now)) return;

        hsIndex = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        hsEphemeral = NewPrivateKey();
        var ephPub = PublicKey(hsEphemeral);
        var c = Hash(Construction);
        var h = Hash(Hash(c, Identifier), cfg.PeerPublicKey);
        c = Kdf(c, ephPub, 1)[0];
        h = Hash(h, ephPub);
        var k = Kdf(c, Dh(hsEphemeral, cfg.PeerPublicKey), 2);
        c = k[0];
        var encStatic = Seal(k[1], 0, staticPub, h);
        h = Hash(h, encStatic);
        k = Kdf(c, Dh(cfg.PrivateKey, cfg.PeerPublicKey), 2);
        c = k[0];
        var encTime = Seal(k[1], 0, Tai64n(DateTimeOffset.UtcNow), h);
        h = Hash(h, encTime);

        var msg = new byte[148];
        msg[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(4), hsIndex);
        ephPub.CopyTo(msg, 8);
        encStatic.CopyTo(msg, 40);
        encTime.CopyTo(msg, 88);
        Mac(mac1Key, msg.AsSpan(0, 116)).CopyTo(msg, 116);
        // mac2 bleibt 0: shortcut: Cookie-Antworten (Server unter Last) werden nicht ausgewertet, der Handshake wird einfach wiederholt

        (hsChain, hsHash, hsSentAt) = (c, h, now);
        Transmit(msg);
    }

    // Handshake-Antwort verarbeiten (Whitepaper 5.4.3); true = neue Sitzung
    bool OnResponse(ReadOnlySpan<byte> msg)
    {
        if (msg.Length != 92 || hsChain is null || hsHash is null || hsEphemeral is null) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(msg[8..]) != hsIndex) return false;
        var remoteIndex = BinaryPrimitives.ReadUInt32LittleEndian(msg[4..]);
        var ephR = msg.Slice(12, 32).ToArray();

        var c = Kdf(hsChain, ephR, 1)[0];
        var h = Hash(hsHash, ephR);
        c = Kdf(c, Dh(hsEphemeral, ephR), 1)[0];
        c = Kdf(c, Dh(cfg.PrivateKey, ephR), 1)[0];
        var k = Kdf(c, cfg.PresharedKey, 3);
        h = Hash(h, k[1]);
        var empty = msg.Slice(44, 16);
        if (Open(k[2], 0, empty, h) is null) return false; // falscher Server-Schlüssel oder manipuliert
        var keys = Kdf(k[0], [], 2);

        var now = Now;
        previous = current;
        current = new Session(hsIndex, remoteIndex, keys[0], keys[1], now);
        (hsChain, hsHash, hsEphemeral, hsSentAt) = (null, null, null, -1);
        lastHandshake = now;
        lastReceived = now;
        return true;
    }

    // Transportpaket entschlüsseln; liefert das IP-Paket (ohne Auffüllung) oder null
    byte[]? OnTransport(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < 32) return null;
        var index = BinaryPrimitives.ReadUInt32LittleEndian(msg[4..]);
        var s = current?.LocalIndex == index ? current : previous?.LocalIndex == index ? previous : null;
        if (s is null || Now - s.Created >= RejectAfter) return null;
        var counter = BinaryPrimitives.ReadUInt64LittleEndian(msg[8..]);
        if (!ReplayOk(s, counter)) return null;
        var plain = Open(s.RecvKey, counter, msg[16..], []);
        if (plain is null) return null;
        MarkSeen(s, counter);
        lastReceived = Now;
        if (plain.Length == 0) return null; // Keepalive
        lastReceivedData = lastReceived;
        // Auffüllung abschneiden: Länge steht im IP-Kopf (IPv6: Nutzlast ohne die 40 Byte Kopf)
        var length = plain[0] >> 4 == 4 && plain.Length >= 20 ? BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(2))
            : plain[0] >> 4 == 6 && plain.Length >= 40 ? 40 + BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(4))
            : plain.Length;
        BytesReceived += length;
        return length <= plain.Length ? plain[..length] : null;
    }

    // shortcut: Replay-Fenster von 64 Paketen statt 8192 wie im Original – sehr stark umsortierte Pakete werden verworfen
    static bool ReplayOk(Session s, ulong counter) =>
        !s.AnyReceived || counter > s.RecvMax || (s.RecvMax - counter < 64 && (s.RecvWindow & (1UL << (int)(s.RecvMax - counter))) == 0);

    static void MarkSeen(Session s, ulong counter)
    {
        if (!s.AnyReceived) (s.AnyReceived, s.RecvMax, s.RecvWindow) = (true, counter, 1);
        else if (counter > s.RecvMax)
        {
            var shift = counter - s.RecvMax;
            s.RecvWindow = shift >= 64 ? 1 : (s.RecvWindow << (int)shift) | 1;
            s.RecvMax = counter;
        }
        else s.RecvWindow |= 1UL << (int)(s.RecvMax - counter);
    }

    bool ResolveEndpoint(double now)
    {
        if (endpoint is not null) return true;
        if (now < nextResolve) return false;
        nextResolve = now + RekeyTimeout;
        try
        {
            var ip = IPAddress.TryParse(cfg.EndpointHost, out var direct) ? direct
                : Dns.GetHostAddresses(cfg.EndpointHost).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            endpoint = new IPEndPoint(ip, cfg.EndpointPort);
            return true;
        }
        catch (Exception e) when (e is SocketException or InvalidOperationException)
        {
            return false;
        }
    }

    void ReceiveLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!cts.IsCancellationRequested)
        {
            byte[] msg;
            try
            {
                msg = udp.Receive(ref any);
            }
            catch (SocketException) when (!cts.IsCancellationRequested)
            {
                continue; // z. B. ICMP "Port nicht erreichbar" vom Server – weiter lauschen
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return;
            }
            if (msg.Length < 4) continue;

            byte[]? packet = null;
            List<byte[]>? flush = null;
            lock (sync)
            {
                switch (msg[0])
                {
                    case 2 when OnResponse(msg):
                        flush = [.. queued];
                        queued.Clear();
                        foreach (var p in flush) SendTransport(current!, p);
                        if (flush.Count == 0) SendTransport(current!, []); // Keepalive bestätigt dem Server die neue Sitzung
                        break;
                    case 4:
                        packet = OnTransport(msg);
                        break;
                    // shortcut: Typ 1 (Server startet Handshake) und 3 (Cookie) werden ignoriert – FlowCap erneuert Sitzungen selbst rechtzeitig
                }
            }
            if (packet is not null) PacketReceived?.Invoke(this, packet);
        }
    }

    void TimerLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            lock (sync)
            {
                var now = Now;
                var stale = current is null || now - current.Created >= RekeyAfter;
                var silent = lastReceived < 0 || now - lastReceived >= IdleCheck; // Lebenszeichen fehlt → Handshake als Test
                if (stale || silent) InitiateHandshake(force: hsSentAt >= 0 && now - hsSentAt >= RekeyTimeout);
                else if (current is not null)
                {
                    var persistent = cfg.PersistentKeepalive > 0 && now - lastSent >= cfg.PersistentKeepalive;
                    var passive = lastReceivedData > lastSent && now - lastSent >= PassiveKeepalive;
                    if (persistent || passive) SendTransport(current, []);
                }
            }
            try { Task.Delay(250, cts.Token).Wait(); } catch (AggregateException) { return; }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        udp.Dispose();
    }
}
