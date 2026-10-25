using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SpeedLimit;

[JsonConverter(typeof(JsonStringEnumConverter<ProxyType>))]
public enum ProxyType { Socks5, Http }

/// <summary>Ein Proxy-Server. Das Passwort liegt nur DPAPI-verschlüsselt (an den Windows-Benutzer gebunden) in der Einstellungsdatei.</summary>
public sealed record ProxyServer(string Name, ProxyType Type, string Host, int Port, string User = "", string ProtectedPassword = "")
{
    public static ProxyServer Create(string name, ProxyType type, string host, int port, string user, string password) =>
        new(name, type, host, port, user, password.Length == 0 ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser)));

    [JsonIgnore]
    public string Password => ProtectedPassword.Length == 0 ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser));

    [JsonIgnore] public string Summary => $"{(Type == ProxyType.Socks5 ? "SOCKS5" : "HTTP")} · {Host}:{Port}" + (User.Length > 0 ? $" · {User}" : "");
}

/// <summary>Baut über einen Proxy eine Verbindung zum Ziel auf (SOCKS5 oder HTTP CONNECT).</summary>
public static class ProxyHandshake
{
    public static async Task<NetworkStream> ConnectAsync(ProxyServer proxy, IPEndPoint target, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, ct);
            var stream = client.GetStream(); // besitzt den Socket: Dispose schließt die Verbindung
            if (proxy.Type == ProxyType.Socks5) await Socks5Async(stream, target, proxy.User, proxy.Password, ct);
            else await HttpConnectAsync(stream, target, proxy.User, proxy.Password, ct);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task Socks5Async(Stream s, IPEndPoint target, string user, string password, CancellationToken ct)
    {
        var auth = user.Length > 0;
        await s.WriteAsync(auth ? new byte[] { 5, 2, 0, 2 } : [5, 1, 0], ct);
        var choice = new byte[2];
        await s.ReadExactlyAsync(choice, ct);
        if (choice[0] != 5) throw new IOException("Kein SOCKS5-Proxy.");
        if (choice[1] == 0xFF) throw new IOException("Der Proxy akzeptiert keine der angebotenen Anmeldearten.");
        if (choice[1] == 2)
        {
            var u = Encoding.UTF8.GetBytes(user);
            var p = Encoding.UTF8.GetBytes(password);
            if (u.Length > 255 || p.Length > 255) throw new IOException("Benutzername oder Passwort zu lang.");
            await s.WriteAsync((byte[])[1, (byte)u.Length, .. u, (byte)p.Length, .. p], ct);
            var status = new byte[2];
            await s.ReadExactlyAsync(status, ct);
            if (status[1] != 0) throw new IOException("Benutzername oder Passwort vom Proxy abgelehnt.");
        }
        else if (choice[1] != 0) throw new IOException("Unbekannte Anmeldeart vom Proxy.");

        var addr = target.Address.GetAddressBytes();
        await s.WriteAsync((byte[])[5, 1, 0, (byte)(addr.Length == 4 ? 1 : 4), .. addr, (byte)(target.Port >> 8), (byte)target.Port], ct);
        var head = new byte[4];
        await s.ReadExactlyAsync(head, ct);
        if (head[1] != 0)
            throw new IOException(head[1] switch
            {
                2 => "Verbindung vom Proxy nicht erlaubt.",
                3 => "Netz nicht erreichbar (Proxy).",
                4 => "Ziel nicht erreichbar (Proxy).",
                5 => "Verbindung vom Ziel abgelehnt (Proxy).",
                _ => $"SOCKS5-Fehler {head[1]}.",
            });
        var rest = head[3] switch { 1 => 4, 4 => 16, 3 => s.ReadByte(), _ => throw new IOException("Ungültige SOCKS5-Antwort.") } + 2;
        await s.ReadExactlyAsync(new byte[rest], ct); // gebundene Adresse + Port, brauchen wir nicht
    }

    public static async Task HttpConnectAsync(Stream s, IPEndPoint target, string user, string password, CancellationToken ct)
    {
        var host = target.ToString(); // IPv6 bereits als [::1]:443
        var auth = user.Length > 0 ? $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))}\r\n" : "";
        await s.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host} HTTP/1.1\r\nHost: {host}\r\n{auth}\r\n"), ct);

        // Byteweise bis zur Leerzeile lesen, damit keine Tunnel-Daten verschluckt werden
        var header = new List<byte>();
        var one = new byte[1];
        while (header.Count < 8192)
        {
            await s.ReadExactlyAsync(one, ct);
            header.Add(one[0]);
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
        }
        var status = Encoding.ASCII.GetString([.. header]).Split("\r\n")[0];
        var parts = status.Split(' ');
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/") || parts[1] != "200")
            throw new IOException(parts.Length >= 2 && parts[1] == "407" ? "Proxy verlangt Anmeldung (407)." : $"Proxy-Antwort: {status}");
    }
}
