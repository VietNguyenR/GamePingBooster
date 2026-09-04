using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GamePingBooster.Core.Protocol;

/// <summary>
/// The profile, sealed to one machine's device key.
///
/// The licence server encrypts a profile to the public key of the device that asked for it; only
/// the holder of the matching private key can open it. That private key is generated on the
/// machine, wrapped with DPAPI at machine scope, and never leaves - so the sealed profile is
/// worthless on any other computer, and worthless to anything on this one that cannot use the
/// device key.
///
/// **Be honest about the ceiling, because it has not moved.** RouteManager turns every CIDR into
/// a Windows route, so while the tunnel is up anyone can read the whole list back with
/// Get-NetRoute. No debugger, no administrator rights. This does not hide the ranges from
/// somebody determined; it makes them hard to TAKE - there is no file to copy, nothing readable
/// crosses the IPC pipe, and a backup of %ProgramData% carries nothing usable.
///
/// What it does buy, precisely:
///
///   - the bytes at rest are meaningless without the device key
///   - the device key is itself DPAPI machine-scoped, so the pair is useless off this machine
///   - the plaintext never touches the disk and never crosses the pipe: the UI fetches a sealed
///     envelope and hands it straight to the service, which is the half that holds the key
///
/// ECIES in the usual shape, with nothing invented:
///
///     ephemeral P-256 keypair, made per profile by the server
///     ECDH(ephemeral private, device public)      -> 32-byte shared secret
///     HKDF-SHA256(secret, salt, info)             -> 32-byte AES key
///     AES-256-GCM(key, nonce, plaintext, aad)     -> ciphertext + tag
///
/// Layout, all fixed except the ciphertext:
///
///     off  len  field
///     0    1    version (0x01)
///     1    65   ephemeral public key, uncompressed P-256
///     66   12   nonce
///     78   4    ciphertext length, big-endian
///     82   N    ciphertext
///     82+N 16   GCM tag
///
/// The header bytes 0..78 are the AAD, so the version, the ephemeral key and the nonce cannot be
/// swapped for others without the tag failing.
/// </summary>
public static class ProfileEnvelope
{
    public const byte Version = 1;

    private const int OffVersion = 0;
    private const int OffEphemeral = 1;
    private const int OffNonce = OffEphemeral + GpbCrypto.PublicKeyLen; // 66
    private const int NonceLen = 12;
    private const int OffLength = OffNonce + NonceLen;                  // 78
    private const int HeaderLen = OffLength + 4;                        // 82
    private const int TagLen = 16;

    /// <summary>Bound into the key derivation so a key from one context cannot serve another.</summary>
    private static readonly byte[] Info = "gpb-profile-v1"u8.ToArray();

    /// <summary>
    /// Opens an envelope with the device's private key.
    /// </summary>
    /// <param name="devicePrivateScalar">The 32-byte P-256 scalar of the device key.</param>
    /// <exception cref="CryptographicException">
    /// Malformed, or not sealed to this device. The two are not distinguished on purpose: a
    /// caller that could tell them apart could use this to test whether a blob belongs to a
    /// given machine.
    /// </exception>
    public static byte[] Open(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> devicePrivateScalar)
    {
        if (envelope.Length < HeaderLen + TagLen)
        {
            throw new CryptographicException("The sealed profile is too short to be one.");
        }
        if (envelope[OffVersion] != Version)
        {
            throw new CryptographicException(
                $"The sealed profile is version {envelope[OffVersion]}, this client understands {Version}.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(envelope.Slice(OffLength, 4));
        if (length > int.MaxValue || HeaderLen + (long)length + TagLen != envelope.Length)
        {
            throw new CryptographicException("The sealed profile's length field does not match its size.");
        }

        var ephemeral = envelope.Slice(OffEphemeral, GpbCrypto.PublicKeyLen);
        var nonce = envelope.Slice(OffNonce, NonceLen);
        var ciphertext = envelope.Slice(HeaderLen, (int)length);
        var tag = envelope.Slice(HeaderLen + (int)length, TagLen);

        var devicePublic = PublicFromScalar(devicePrivateScalar);
        var key = DeriveKeyFromParts(devicePrivateScalar, ephemeral, ephemeral, devicePublic);
        try
        {
            var plaintext = new byte[length];
            using var gcm = new AesGcm(key, TagLen);
            // The header is the AAD, so nothing in it can be swapped without the tag failing.
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, envelope[..HeaderLen]);
            return plaintext;
        }
        catch (CryptographicException)
        {
            // Deliberately vague, and deliberately the same message as a malformed envelope.
            throw new CryptographicException("The sealed profile is not for this device.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Seals a profile to a device public key. Used only by tests and by the cross-language
    /// check - the licence server does this in production, in a different language.
    /// </summary>
    public static byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> devicePublicKey)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublic = ExportPublic(ephemeral);

        var scalar = ephemeral.ExportParameters(true).D!;
        byte[] key;
        try
        {
            key = DeriveKeyFromParts(scalar, devicePublicKey, ephemeralPublic, devicePublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }

        try
        {
            var envelope = new byte[HeaderLen + plaintext.Length + TagLen];
            envelope[OffVersion] = Version;
            ephemeralPublic.CopyTo(envelope.AsSpan(OffEphemeral));
            RandomNumberGenerator.Fill(envelope.AsSpan(OffNonce, NonceLen));
            BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(OffLength, 4), (uint)plaintext.Length);

            using var gcm = new AesGcm(key, TagLen);
            gcm.Encrypt(
                envelope.AsSpan(OffNonce, NonceLen),
                plaintext,
                envelope.AsSpan(HeaderLen, plaintext.Length),
                envelope.AsSpan(HeaderLen + plaintext.Length, TagLen),
                envelope.AsSpan(0, HeaderLen));

            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// The AES key, derived identically on both sides.
    ///
    /// Both the server and the client know all four values below; what matters is that they
    /// assemble the salt in the SAME order. It is always ephemeral public first, then device
    /// public - never "mine then theirs", which would differ between the two sides and produce
    /// two different keys whose only symptom is a tag that will not verify.
    /// </summary>
    private static byte[] DeriveKeyFromParts(
        ReadOnlySpan<byte> ownPrivateScalar,
        ReadOnlySpan<byte> peerPublic,
        ReadOnlySpan<byte> ephemeralPublic,
        ReadOnlySpan<byte> devicePublic)
    {
        using var self = ECDiffieHellman.Create();
        self.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = ownPrivateScalar.ToArray(),
        });

        using var peer = ECDiffieHellman.Create();
        peer.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = peerPublic.Slice(1, 32).ToArray(),
                Y = peerPublic.Slice(33, 32).ToArray(),
            },
        });

        // The RAW agreement, not the hashed one. Both sides have to agree on the hashing, and
        // doing it in HKDF below keeps that decision in one place instead of two.
        var secret = self.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            var salt = new byte[GpbCrypto.PublicKeyLen * 2];
            ephemeralPublic.CopyTo(salt);
            devicePublic.CopyTo(salt.AsSpan(GpbCrypto.PublicKeyLen));

            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] ExportPublic(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        var raw = new byte[GpbCrypto.PublicKeyLen];
        raw[0] = 0x04;
        // Right-aligned, the same trap GpbCrypto.ExportPublicKey documents: a coordinate shorter
        // than the field size turns up about one key in 256.
        p.Q.X!.CopyTo(raw, 1 + (32 - p.Q.X!.Length));
        p.Q.Y!.CopyTo(raw, 33 + (32 - p.Q.Y!.Length));
        return raw;
    }

    private static byte[] PublicFromScalar(ReadOnlySpan<byte> scalar)
    {
        using var key = ECDiffieHellman.Create();
        key.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = scalar.ToArray(),
        });
        return ExportPublic(key);
    }
}
