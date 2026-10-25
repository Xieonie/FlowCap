using System.IO;
using System.Net;

namespace SpeedLimit;

/// <summary>Die Teile einer WireGuard-.conf, die FlowCap braucht ([Interface] + erster [Peer]).</summary>
public sealed record WireGuardConfig(
    byte[] PrivateKey, IPAddress Address, byte[] PeerPublicKey, byte[] PresharedKey,
    string EndpointHost, int EndpointPort, int PersistentKeepalive, int Mtu)
{
    /// <summary>Liest eine .conf; wirft <see cref="FormatException"/> mit verständlicher Meldung bei Fehlern.</summary>
    public static WireGuardConfig Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        var peers = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                if (section == "peer") peers++;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0 || section is null) continue;
            if (section == "peer" && peers > 1) continue; // shortcut: nur der erste Peer wird genutzt, Anbieter-Configs haben genau einen
            values.TryAdd($"{section}.{line[..eq].Trim()}", line[(eq + 1)..].Trim());
        }

        string Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0
            ? v : throw new FormatException($"In der Config fehlt „{key.Split('.')[1]}“ im Abschnitt [{key.Split('.')[0]}].");

        var address = Get("interface.Address").Split(',')
            .Select(a => a.Trim().Split('/')[0])
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .FirstOrDefault(ip => ip?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? throw new FormatException("Die Config hat keine IPv4-Adresse unter [Interface] Address – FlowCap braucht eine.");

        var endpoint = Get("peer.Endpoint");
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out var port) || port is < 1 or > 65535)
            throw new FormatException($"Ungültiger Endpoint „{endpoint}“ (erwartet Server:Port).");

        return new(
            Key(Get("interface.PrivateKey"), "PrivateKey"),
            address,
            Key(Get("peer.PublicKey"), "PublicKey"),
            values.TryGetValue("peer.PresharedKey", out var psk) ? Key(psk, "PresharedKey") : new byte[32],
            endpoint[..colon].Trim('[', ']'),
            port,
            values.TryGetValue("peer.PersistentKeepalive", out var ka) && int.TryParse(ka, out var k) ? k : 0,
            values.TryGetValue("interface.MTU", out var mtu) && int.TryParse(mtu, out var m) ? m : 1420);
    }

    static byte[] Key(string base64, string name)
    {
        try
        {
            var key = Convert.FromBase64String(base64);
            if (key.Length == 32) return key;
        }
        catch (FormatException)
        {
        }
        throw new FormatException($"{name} ist kein gültiger WireGuard-Schlüssel.");
    }

    public static WireGuardConfig Load(string path) => Parse(File.ReadAllText(path));
}
