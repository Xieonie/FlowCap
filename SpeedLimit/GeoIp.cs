using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace SpeedLimit;

/// <summary>
/// Land zu einer IP-Adresse – offline aus der mitgelieferten Datenbank „IP to Country Lite“ von DB-IP (CC BY 4.0),
/// es geht also keine Adresse an einen fremden Dienst. Wird beim ersten Zugriff im Hintergrund geladen (~1 s).
/// </summary>
public sealed class GeoIp
{
    readonly uint[] v4Start, v4End;
    readonly UInt128[] v6Start, v6End;
    readonly string[] v4Cc, v6Cc;

    static readonly Lazy<Task<GeoIp?>> Shared = new(() => Task.Run(LoadEmbedded));

    /// <summary>Datenbank, sobald geladen; null solange sie lädt oder fehlt.</summary>
    public static GeoIp? Current => Shared.Value.IsCompletedSuccessfully ? Shared.Value.Result : null;

    GeoIp(List<(uint S, uint E, string Cc)> v4, List<(UInt128 S, UInt128 E, string Cc)> v6)
    {
        (v4Start, v4End, v4Cc) = ([.. v4.Select(r => r.S)], [.. v4.Select(r => r.E)], [.. v4.Select(r => r.Cc)]);
        (v6Start, v6End, v6Cc) = ([.. v6.Select(r => r.S)], [.. v6.Select(r => r.E)], [.. v6.Select(r => r.Cc)]);
    }

    static GeoIp? LoadEmbedded()
    {
        using var res = typeof(GeoIp).Assembly.GetManifestResourceStream("SpeedLimit.Data.dbip-country-lite.csv.gz");
        if (res is null) return null;
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        return Parse(new StreamReader(gz));
    }

    /// <summary>CSV „start,ende,land“, aufsteigend sortiert wie bei DB-IP.</summary>
    public static GeoIp Parse(TextReader csv)
    {
        var v4 = new List<(uint, uint, string)>();
        var v6 = new List<(UInt128, UInt128, string)>();
        while (csv.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length < 3 || parts[2] == "ZZ" || !IPAddress.TryParse(parts[0], out var s) || !IPAddress.TryParse(parts[1], out var e)) continue;
            if (s.AddressFamily == AddressFamily.InterNetwork) v4.Add((V4(s), V4(e), parts[2]));
            else v6.Add((V6(s), V6(e), parts[2]));
        }
        return new GeoIp(v4, v6);
    }

    /// <summary>Ländercode (ISO, z. B. "DE") oder null bei privaten/unbekannten Adressen.</summary>
    public string? CountryOf(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.AddressFamily == AddressFamily.InterNetwork ? Find(v4Start, v4End, v4Cc, V4(ip)) : Find(v6Start, v6End, v6Cc, V6(ip));
    }

    static string? Find<T>(T[] starts, T[] ends, string[] cc, T value) where T : IComparable<T>
    {
        var i = Array.BinarySearch(starts, value);
        if (i < 0) i = ~i - 1; // letzter Bereich, der davor beginnt
        return i >= 0 && value.CompareTo(ends[i]) <= 0 ? cc[i] : null;
    }

    static uint V4(IPAddress ip) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    static UInt128 V6(IPAddress ip) => System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(ip.GetAddressBytes());

    static readonly Lazy<System.IO.Compression.ZipArchive?> FlagZip = new(() =>
        typeof(GeoIp).Assembly.GetManifestResourceStream("SpeedLimit.Data.flags-w40.zip") is { } s ? new ZipArchive(s) : null);
    static readonly Dictionary<string, System.Windows.Media.ImageSource?> Flags = [];

    /// <summary>Flagge zum Ländercode (40 px breit, aus der mitgelieferten Sammlung von flagcdn.com); null, wenn es keine gibt. Nur im UI-Thread.</summary>
    public static System.Windows.Media.ImageSource? FlagOf(string code)
    {
        code = code.ToLowerInvariant();
        if (Flags.TryGetValue(code, out var cached)) return cached;
        System.Windows.Media.Imaging.BitmapImage? img = null;
        if (FlagZip.Value?.GetEntry(code + ".png") is { } entry)
        {
            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            ms.Position = 0;
            img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // Stream danach nicht mehr nötig
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
        }
        return Flags[code] = img;
    }

    /// <summary>"DE" → "Deutschland" (in der Sprache von Windows); unbekannte Codes bleiben stehen.</summary>
    public static string CountryName(string code)
    {
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }
}
