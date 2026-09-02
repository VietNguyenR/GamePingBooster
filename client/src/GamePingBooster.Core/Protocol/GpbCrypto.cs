using System.Security.Cryptography;

namespace GamePingBooster.Core.Protocol;

/// <summary>
/// P-256 signing and verification, the C# half of relay/internal/protocol/crypto.go.
///
/// Why P-256 and not Ed25519, which would be the obvious modern choice: the rule is standard
/// library only on BOTH sides, and .NET 9 has no Ed25519 and no X25519 - checked against the
/// installed reference assemblies, not assumed. It does have ECDsa, ECDiffieHellman with
/// DeriveRawSecretAgreement, HKDF and AesGcm. Ed25519 would mean a NuGet dependency in a client
/// that deliberately has none. See docs/PROTOCOL-v3.md.
///
/// Signatures are the raw 64-byte r||s pair, not ASN.1 DER, because a fixed-layout wire format
/// cannot carry a variable-length field. This happens to be exactly what ECDsa.SignHash and
/// VerifyHash already produce and expect, so there is no conversion on this side. Go has to pad
/// r and s itself; see the note there.
/// </summary>
public static class GpbCrypto
{
    /// <summary>An uncompressed P-256 point: 0x04 || X(32) || Y(32).</summary>
    public const int PublicKeyLen = 65;

    /// <summary>r || s, each padded to the 32-byte field size.</summary>
    public const int SignatureLen = 64;

    /// <summary>The scalar d, big-endian.</summary>
    public const int PrivateKeyLen = 32;

    private const int FieldLen = 32;

    /// <summary>Builds a verify-only key from the 65-byte uncompressed form.</summary>
    /// <exception cref="CryptographicException">The bytes are not a point on P-256.</exception>
    public static ECDsa ImportPublicKey(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != PublicKeyLen || raw[0] != 0x04)
        {
            throw new CryptographicException("not a valid uncompressed P-256 public key");
        }

        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = raw.Slice(1, FieldLen).ToArray(),
                Y = raw.Slice(1 + FieldLen, FieldLen).ToArray(),
            },
        };
        // Validate() is what refuses a point that is not on the curve. Without it a forged key
        // is accepted and signatures are then verified against something that is not P-256 at
        // all - a real attack, not a theoretical one.
        p.Validate();

        var key = ECDsa.Create();
        key.ImportParameters(p);
        return key;
    }

    /// <summary>Builds a signing key from the 32-byte scalar.</summary>
    /// <exception cref="CryptographicException">The scalar is not a valid P-256 private key.</exception>
    public static ECDsa ImportPrivateKey(ReadOnlySpan<byte> d)
    {
        if (d.Length != PrivateKeyLen)
        {
            throw new CryptographicException("a P-256 private key is 32 bytes");
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        key.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = d.ToArray(),
        });
        return key;
    }

    /// <summary>The 65-byte uncompressed encoding of a key's public half.</summary>
    public static byte[] ExportPublicKey(ECDsa key)
    {
        var p = key.ExportParameters(false);
        var raw = new byte[PublicKeyLen];
        raw[0] = 0x04;
        // X and Y come back without leading zeros, so they are right-aligned into fixed fields.
        // Copying them to offset 1 and 33 unconditionally would silently shift a key whose
        // coordinate happens to be small - about one key in 256.
        p.Q.X!.CopyTo(raw, 1 + (FieldLen - p.Q.X!.Length));
        p.Q.Y!.CopyTo(raw, 1 + FieldLen + (FieldLen - p.Q.Y!.Length));
        return raw;
    }

    /// <summary>Signs SHA-256 of <paramref name="message"/>, returning the raw 64-byte r||s.</summary>
    public static byte[] Sign(ECDsa key, ReadOnlySpan<byte> message)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(message, digest);
        return key.SignHash(digest);
    }

    /// <summary>Verifies a raw 64-byte r||s signature over SHA-256 of <paramref name="message"/>.</summary>
    public static bool Verify(ECDsa key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLen)
        {
            return false;
        }
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(message, digest);
        return key.VerifyHash(digest, signature);
    }
}
