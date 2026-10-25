using System.Net;
using System.Net.Sockets;
using System.Text;
using SpeedLimit;

public class ProxyHandshakeTests
{
    static readonly IPEndPoint Target = new(IPAddress.Parse("1.2.3.4"), 443);

    /// <summary>Startet einen Mini-Proxy auf localhost, der das Skript <paramref name="serve"/> abarbeitet.</summary>
    static async Task<string> RunAsync(Func<NetworkStream, Task> serve, Func<Stream, Task> client)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            await serve(c.GetStream());
        });
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        try
        {
            await client(tcp.GetStream());
            return "ok";
        }
        catch (IOException e)
        {
            return e.Message;
        }
        finally
        {
            await server;
            listener.Stop();
        }
    }

    static async Task<byte[]> Read(Stream s, int n)
    {
        var b = new byte[n];
        await s.ReadExactlyAsync(b);
        return b;
    }

    [Fact]
    public async Task Socks5_WithPassword_SendsCredentialsAndTarget()
    {
        byte[]? request = null, login = null;
        var result = await RunAsync(async s =>
        {
            Assert.Equal(new byte[] { 5, 2, 0, 2 }, await Read(s, 4));
            await s.WriteAsync(new byte[] { 5, 2 });
            login = await Read(s, 1 + 1 + 4 + 1 + 6); // ver, len, "alex", len, "geheim"
            await s.WriteAsync(new byte[] { 1, 0 });
            request = await Read(s, 10);
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        }, c => ProxyHandshake.Socks5Async(c, Target, "alex", "geheim", default));

        Assert.Equal("ok", result);
        Assert.Equal("alex", Encoding.UTF8.GetString(login!, 2, 4));
        Assert.Equal(new byte[] { 5, 1, 0, 1, 1, 2, 3, 4, 1, 187 }, request);
    }

    [Fact]
    public async Task Socks5_ErrorReplyIsReported()
    {
        var result = await RunAsync(async s =>
        {
            await Read(s, 3);
            await s.WriteAsync(new byte[] { 5, 0 });
            await Read(s, 10);
            await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
        }, c => ProxyHandshake.Socks5Async(c, Target, "", "", default));
        Assert.Contains("abgelehnt", result);
    }

    [Fact]
    public async Task Http_Connect200_LeavesTunnelDataUnread()
    {
        string? sent = null;
        var tunnel = "";
        var result = await RunAsync(async s =>
        {
            var buf = new byte[1024];
            var n = await s.ReadAsync(buf);
            sent = Encoding.ASCII.GetString(buf, 0, n);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\nHALLO"));
        }, async c =>
        {
            await ProxyHandshake.HttpConnectAsync(c, Target, "u", "p", default);
            tunnel = Encoding.ASCII.GetString(await Read(c, 5));
        });

        Assert.Equal("ok", result);
        Assert.StartsWith("CONNECT 1.2.3.4:443 HTTP/1.1\r\n", sent);
        Assert.Contains("Proxy-Authorization: Basic dTpw", sent); // base64("u:p")
        Assert.Equal("HALLO", tunnel);
    }

    [Fact]
    public async Task Http_407IsReported()
    {
        var result = await RunAsync(async s =>
        {
            await s.ReadAsync(new byte[1024]);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"));
        }, c => ProxyHandshake.HttpConnectAsync(c, Target, "", "", default));
        Assert.Contains("407", result);
    }

    [Fact]
    public void Password_IsStoredEncrypted()
    {
        var p = ProxyServer.Create("x", ProxyType.Socks5, "h", 1080, "u", "geheim");
        Assert.DoesNotContain("geheim", p.ProtectedPassword);
        Assert.Equal("geheim", p.Password);
    }
}
