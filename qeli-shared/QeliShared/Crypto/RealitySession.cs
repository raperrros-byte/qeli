using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace Qeli.Shared.Crypto;

/// <summary>REALITY-style session_id token for fake-tls profiles with reality_proxy.
/// Mirrors <c>crypto/reality.rs::seal_session_id</c>.</summary>
public static class RealitySession
{
    private const int ShortIdLen = 8;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("qeli-reality-sid-v1");

    /// <summary>Parse hex short_id into 8 bytes (zero-padded; extra hex ignored).</summary>
    public static byte[] ShortIdFromHex(string hex)
    {
        var out_ = new byte[ShortIdLen];
        var digits = hex.Where(Uri.IsHexDigit).ToArray();
        int i = 0;
        while (i / 2 < ShortIdLen && i + 1 < digits.Length)
        {
            int hi = HexVal(digits[i]);
            int lo = HexVal(digits[i + 1]);
            out_[i / 2] = (byte)((hi << 4) | lo);
            i += 2;
        }
        return out_;
    }

    /// <summary>Seal short_id + unix timestamp into a 32-byte TLS session_id using the
    /// ephemeral keypair that is also sent as the ClientHello key_share.</summary>
    public static byte[] SealSessionId(byte[] realityPubRaw, byte[] ephemeralPriv, byte[] ephemeralPub, byte[] shortId)
    {
        if (realityPubRaw.Length != 32 || ephemeralPriv.Length != 32 || ephemeralPub.Length != 32 || shortId.Length != ShortIdLen)
            throw new ArgumentException("invalid reality seal inputs");

        var shared = new byte[X25519.PointSize];
        if (!X25519.CalculateAgreement(ephemeralPriv, 0, realityPubRaw, 0, shared, 0))
            throw new CryptographicException("X25519 agreement failed for reality seal");

        var (key, nonce) = DeriveKeyNonce(shared);
        CryptographicOperations.ZeroMemory(shared);

        var pt = new byte[ShortIdLen + 8];
        Buffer.BlockCopy(shortId, 0, pt, 0, ShortIdLen);
        Buffer.BlockCopy(BitConverter.GetBytes((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), 0, pt, ShortIdLen, 8);

        var ct = new PacketCipher(key).Encrypt(pt, nonce);
        if (ct.Length != 32) throw new InvalidOperationException("reality seal must be 32 bytes");
        return ct;
    }

    private static (byte[] key, byte[] nonce) DeriveKeyNonce(byte[] shared)
    {
        var prk = Hmac(Array.Empty<byte>(), shared);
        var okm = Expand(prk, Info, 44);
        var key = okm[..32];
        var nonce = okm[32..44];
        CryptographicOperations.ZeroMemory(prk);
        CryptographicOperations.ZeroMemory(okm);
        return (key, nonce);
    }

    private static byte[] Hmac(byte[] key, byte[] data)
    {
        using var mac = new HMACSHA256(key);
        return mac.ComputeHash(data);
    }

    private static byte[] Expand(byte[] prk, byte[] info, int length)
    {
        using var mac = new HMACSHA256(prk);
        var result = new byte[length];
        var t = Array.Empty<byte>();
        int offset = 0;
        byte blockIndex = 1;
        while (offset < length)
        {
            mac.Initialize();
            var input = new byte[t.Length + info.Length + 1];
            Buffer.BlockCopy(t, 0, input, 0, t.Length);
            Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
            input[^1] = blockIndex;
            t = mac.ComputeHash(input);
            int copyLen = Math.Min(t.Length, length - offset);
            Buffer.BlockCopy(t, 0, result, offset, copyLen);
            offset += copyLen;
            blockIndex++;
        }
        return result;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}
