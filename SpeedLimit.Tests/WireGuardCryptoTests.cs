using System.Text;
using SpeedLimit;

public class WireGuardCryptoTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void X25519_Rfc7748Vectors()
    {
        var alicePriv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPriv = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        Assert.Equal(Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"), WireGuardCrypto.PublicKey(alicePriv));
        Assert.Equal(Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"), WireGuardCrypto.PublicKey(bobPriv));
        var shared = Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        Assert.Equal(shared, WireGuardCrypto.Dh(alicePriv, WireGuardCrypto.PublicKey(bobPriv)));
        Assert.Equal(shared, WireGuardCrypto.Dh(bobPriv, WireGuardCrypto.PublicKey(alicePriv)));
    }

    [Fact]
    public void Blake2s_ReferenceValue()
    {
        Assert.Equal(Hex("508C5E8C327C14E2E1A72BA34EEB452F37458B209ED63A294D999B4C86675982"),
            WireGuardCrypto.Hash(Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void Aead_RoundTripAndTamperDetection()
    {
        var key = new byte[32];
        key[0] = 7;
        var sealedData = WireGuardCrypto.Seal(key, 42, "hallo"u8, "ad"u8);
        Assert.Equal("hallo"u8.ToArray(), WireGuardCrypto.Open(key, 42, sealedData, "ad"u8));
        Assert.Null(WireGuardCrypto.Open(key, 43, sealedData, "ad"u8)); // falscher Zähler
        sealedData[0] ^= 1;
        Assert.Null(WireGuardCrypto.Open(key, 42, sealedData, "ad"u8));
    }

    [Fact]
    public void Kdf_ProducesDistinctKeys()
    {
        var keys = WireGuardCrypto.Kdf(new byte[32], [1, 2, 3], 3);
        Assert.Equal(3, keys.Length);
        Assert.NotEqual(keys[0], keys[1]);
        Assert.Equal(keys[0], WireGuardCrypto.Kdf(new byte[32], [1, 2, 3], 1)[0]); // Präfix-stabil
    }
}
