using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace SpeedLimit;

/// <summary>Die Primitive aus dem WireGuard-Whitepaper (Abschnitt 5.4): BLAKE2s, HMAC-BLAKE2s, HKDF, X25519, ChaCha20-Poly1305.</summary>
public static class WireGuardCrypto
{
    public static readonly byte[] Construction = Encoding.ASCII.GetBytes("Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s");
    public static readonly byte[] Identifier = Encoding.ASCII.GetBytes("WireGuard v1 zx2c4 Jason@zx2c4.com");
    public static readonly byte[] LabelMac1 = Encoding.ASCII.GetBytes("mac1----");

    public static byte[] Hash(params byte[][] parts)
    {
        var d = new Blake2sDigest(256);
        foreach (var p in parts) d.BlockUpdate(p, 0, p.Length);
        var output = new byte[32];
        d.DoFinal(output, 0);
        return output;
    }

    /// <summary>Keyed BLAKE2s mit 16 Byte Ausgabe (mac1/mac2).</summary>
    public static byte[] Mac(byte[] key, ReadOnlySpan<byte> input)
    {
        var d = new Blake2sDigest(key, 16, null, null);
        d.BlockUpdate(input.ToArray(), 0, input.Length);
        var output = new byte[16];
        d.DoFinal(output, 0);
        return output;
    }

    public static byte[] Hmac(byte[] key, byte[] input)
    {
        var h = new HMac(new Blake2sDigest(256));
        h.Init(new KeyParameter(key));
        h.BlockUpdate(input, 0, input.Length);
        var output = new byte[32];
        h.DoFinal(output, 0);
        return output;
    }

    /// <summary>KDF_n aus dem Whitepaper: liefert n Schlüssel à 32 Byte.</summary>
    public static byte[][] Kdf(byte[] key, byte[] input, int n)
    {
        var t0 = Hmac(key, input);
        var result = new byte[n][];
        var prev = Array.Empty<byte>();
        for (var i = 0; i < n; i++)
        {
            prev = Hmac(t0, [.. prev, (byte)(i + 1)]);
            result[i] = prev;
        }
        return result;
    }

    public static byte[] PublicKey(byte[] privateKey)
    {
        var pub = new byte[32];
        X25519.ScalarMultBase(privateKey, 0, pub, 0);
        return pub;
    }

    public static byte[] NewPrivateKey() => RandomNumberGenerator.GetBytes(32); // X25519 klemmt den Skalar selbst

    public static byte[] Dh(byte[] privateKey, byte[] publicKey)
    {
        var shared = new byte[32];
        X25519.ScalarMult(privateKey, 0, publicKey, 0, shared, 0);
        return shared;
    }

    static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12]; // 4 Nullbytes + 64-Bit-Zähler little-endian
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public static byte[] Seal(byte[] key, ulong counter, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> ad)
    {
        using var aead = new ChaCha20Poly1305(key);
        var output = new byte[plaintext.Length + 16];
        aead.Encrypt(Nonce(counter), plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ad);
        return output;
    }

    /// <summary>Entschlüsselt; null, wenn die Prüfsumme nicht stimmt.</summary>
    public static byte[]? Open(byte[] key, ulong counter, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> ad)
    {
        if (ciphertext.Length < 16) return null;
        using var aead = new ChaCha20Poly1305(key);
        var output = new byte[ciphertext.Length - 16];
        try
        {
            aead.Decrypt(Nonce(counter), ciphertext[..^16], ciphertext[^16..], output, ad);
            return output;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    /// <summary>TAI64N-Zeitstempel (12 Byte) für die Handshake-Initiierung.</summary>
    public static byte[] Tai64n(DateTimeOffset now)
    {
        var t = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(t, 0x400000000000000aUL + (ulong)now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), (uint)(now.Ticks % TimeSpan.TicksPerSecond * 100));
        return t;
    }
}
