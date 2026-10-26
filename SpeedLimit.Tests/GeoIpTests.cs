using System.Net;
using SpeedLimit;

public class GeoIpTests
{
    const string Csv = """
        0.0.0.0,0.255.255.255,ZZ
        1.0.0.0,1.0.0.255,AU
        1.0.4.0,1.0.7.255,AU
        8.8.8.0,8.8.8.255,US
        2001:4860::,2001:4860:ffff:ffff:ffff:ffff:ffff:ffff,US
        """;

    [Theory]
    [InlineData("1.0.0.7", "AU")]
    [InlineData("8.8.8.8", "US")]
    [InlineData("::ffff:8.8.8.8", "US")]          // IPv4 in IPv6-Schreibweise
    [InlineData("2001:4860:4860::8888", "US")]
    [InlineData("1.0.2.1", null)]                // Lücke zwischen zwei Bereichen
    [InlineData("0.1.2.3", null)]                // ZZ = unbekannt
    [InlineData("192.168.1.1", null)]
    public void CountryOf_FindsRange(string ip, string? cc) =>
        Assert.Equal(cc, GeoIp.Parse(new StringReader(Csv)).CountryOf(IPAddress.Parse(ip)));

    [Fact]
    public void Flags_EmbeddedForCountries()
    {
        var de = GeoIp.FlagOf("DE");
        Assert.NotNull(de);
        Assert.Equal(40, ((System.Windows.Media.Imaging.BitmapSource)de).PixelWidth);
        Assert.Same(de, GeoIp.FlagOf("de")); // zwischengespeichert
        Assert.Null(GeoIp.FlagOf("XX"));
    }

    [Fact]
    public void EmbeddedDatabase_Loads()
    {
        using var res = typeof(GeoIp).Assembly.GetManifestResourceStream("SpeedLimit.Data.dbip-country-lite.csv.gz");
        Assert.NotNull(res);
        using var gz = new System.IO.Compression.GZipStream(res, System.IO.Compression.CompressionMode.Decompress);
        var db = GeoIp.Parse(new StreamReader(gz));
        Assert.Equal("US", db.CountryOf(IPAddress.Parse("8.8.8.8")));
    }
}
