using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace SpeedLimit;

/// <summary>
/// Lokales Relay für "Proxy pro Programm": Der Throttler spiegelt TCP-Verbindungen eines Programms hierher,
/// das Relay baut über den zugewiesenen Proxy die Verbindung zum ursprünglichen Ziel auf und reicht die Daten durch.
/// </summary>
public sealed class ProxyRelay : IDisposable
{
    const string FirewallRule = "FlowCap Proxy-Relay";

    /// <summary>Ursprüngliches Ziel einer umgeleiteten Verbindung, Schlüssel ist der lokale Port des Programms.</summary>
    public readonly record struct Nat(byte[] Remote, ushort RemotePort, string App, ProxyServer Proxy);

    readonly TcpListener listener;
    readonly PortMap ports;
    readonly ConcurrentDictionary<ushort, Nat> nat = new();
    // Lokaler Port → Programm direkt aus Socket-Ereignissen: bekannt, bevor das erste Paket rausgeht (PortMap hinkt bis zu 1 s hinterher)
    readonly ConcurrentDictionary<(bool Tcp, ushort Port), string> seen = new();
    readonly IntPtr socketHandle;
    readonly CancellationTokenSource cts = new();
    readonly string self = Process.GetCurrentProcess().ProcessName;
    volatile Dictionary<string, ProxyServer> proxies = [];

    public ushort Port { get; }

    /// <summary>Letzter Fehler beim Verbinden über einen Proxy (für die Anzeige).</summary>
    public string? LastError { get; private set; }

    public ProxyRelay(PortMap ports)
    {
        this.ports = ports;
        EnsureFirewallRule();
        listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        Port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        // BIND liefert den lokalen UDP-Port schon vor dem ersten Senden, CONNECT den von TCP-Verbindungen
        socketHandle = WinDivert.WinDivertOpen("(event == BIND or event == CONNECT) and not loopback", WinDivert.LayerSocket, 0,
            WinDivert.FlagSniff | WinDivert.FlagRecvOnly);
        if (socketHandle != new IntPtr(-1)) new Thread(Watch) { IsBackground = true, Name = "SocketWatch" }.Start();
        _ = AcceptLoop();
    }

    public void SetProxies(Dictionary<string, ProxyServer> map) => proxies = map;

    /// <summary>Zugewiesener Proxy des Programms; FlowCap selbst nie (sonst liefe das Relay im Kreis).</summary>
    public ProxyServer? ProxyFor(string? app) => app is null || app == self ? null : proxies.GetValueOrDefault(app);

    public string? Lookup(bool tcp, ushort localPort) => seen.GetValueOrDefault((tcp, localPort));

    public void Register(ushort localPort, byte[] remote, ushort remotePort, string app, ProxyServer proxy) =>
        nat[localPort] = new(remote, remotePort, app, proxy);

    public bool TryGetNat(ushort localPort, out Nat entry) => nat.TryGetValue(localPort, out entry);

    void Watch()
    {
        while (!cts.IsCancellationRequested && WinDivert.WinDivertRecv(socketHandle, null, 0, out _, out var addr))
        {
            if (addr.LocalPort != 0 && ports.NameOf((int)addr.ProcessId) is { } name) seen[(addr.Protocol == 6, addr.LocalPort)] = name;
        }
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Serve(client);
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            var peer = (IPEndPoint)client.Client.RemoteEndPoint!;
            var localPort = (ushort)peer.Port;
            // Nur Verbindungen annehmen, die FlowCap selbst umgeleitet hat – sonst wäre das Relay ein offener Proxy im Netz
            if (!nat.TryGetValue(localPort, out var entry) || !peer.Address.MapToIPv4().GetAddressBytes().AsSpan().SequenceEqual(entry.Remote)) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await using var upstream = await ProxyHandshake.ConnectAsync(entry.Proxy, new IPEndPoint(new IPAddress(entry.Remote), entry.RemotePort), timeout.Token);
                client.NoDelay = true;
                var local = client.GetStream();
                await Task.WhenAll(Pump(local, upstream, cts.Token), Pump(upstream, local, cts.Token));
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                LastError = $"{DateTime.Now:HH:mm:ss} {entry.App} über {entry.Proxy.Name}: {e.Message}";
            }
            finally
            {
                // Etwas später aufräumen, damit letzte FIN/ACK-Pakete noch umgeleitet werden; neuere Einträge desselben Ports bleiben
                _ = Task.Delay(5000).ContinueWith(_ => nat.TryRemove(new KeyValuePair<ushort, Nat>(localPort, entry)));
            }
        }
    }

    // Eine Richtung kopieren und danach die Gegenseite halb schließen, damit HTTP & Co. das Ende sehen
    static async Task Pump(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        try
        {
            await from.CopyToAsync(to, ct);
            to.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    // Die gespiegelten Pakete kommen für Windows "von außen" an – ohne Freigabe würde die Firewall sie blockieren
    static void EnsureFirewallRule()
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        Netsh($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP program=\"{Environment.ProcessPath}\" enable=yes profile=any");
    }

    static void Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
        if (socketHandle == new IntPtr(-1)) return;
        WinDivert.WinDivertShutdown(socketHandle, 3);
        WinDivert.WinDivertClose(socketHandle);
    }
}
