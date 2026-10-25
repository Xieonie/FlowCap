using SpeedLimit;

public class WireGuardConfigTests
{
    const string Sample = """
        [Interface]
        # Proton-ähnliche Config
        PrivateKey = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=
        Address = 10.2.0.2/32, fd00::2/128
        DNS = 10.2.0.1

        [Peer]
        PublicKey = QP+A67Z2UBrMgvNIdHv8gPel5URWNLS4B3ZQ2hQIZlg=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = 185.159.157.1:51820
        PersistentKeepalive = 25
        """;

    [Fact]
    public void Parse_ProviderConfig()
    {
        var c = WireGuardConfig.Parse(Sample);
        Assert.Equal("10.2.0.2", c.Address.ToString());
        Assert.Equal("185.159.157.1", c.EndpointHost);
        Assert.Equal(51820, c.EndpointPort);
        Assert.Equal(25, c.PersistentKeepalive);
        Assert.Equal(32, c.PrivateKey.Length);
        Assert.Equal(new byte[32], c.PresharedKey);
        Assert.Equal(1420, c.Mtu);
    }

    [Theory]
    [InlineData("PrivateKey = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", "PrivateKey = kaputt", "PrivateKey")]
    [InlineData("Endpoint = 185.159.157.1:51820", "Endpoint = ohne-port", "Endpoint")]
    [InlineData("Address = 10.2.0.2/32, fd00::2/128", "Address = fd00::2/128", "IPv4")]
    public void Parse_ReportsProblems(string good, string bad, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => WireGuardConfig.Parse(Sample.Replace(good, bad)));
        Assert.Contains(expected, ex.Message);
    }
}
