using System.Security.Cryptography;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.Service;

/// <summary>
/// The machine's P-256 keypair. This is what a licence token is issued TO.
///
/// Do not confuse it with <see cref="ClientIdentity"/>, which sits in the same directory and is
/// a different thing entirely. The client id is a random 64-bit number whose only job is to let
/// a reconnecting client keep the inner address it had; it is disposable, and losing it costs
/// one rebuild of the routing table. This keypair is the device's NAME. The licence server
/// registers its public half against an account, counts it against the account's device limit,
/// and writes it into every token it signs. Losing it burns a device slot.
///
/// The order is the opposite of the intuitive one, and is deliberate (docs/COMMERCIAL.md):
///
///     install / first run   ->  generate the keypair       (no account involved yet)
///     user signs in         ->  send the PUBLIC key up
///     server                ->  registers it, or refuses   (device limit)
///
/// The keypair identifies the MACHINE; the account identifies the PERSON. They are bound at
/// sign-in, not at generation - which is why this runs with no reference to any account and
/// works perfectly on a machine that has never signed in to anything.
///
/// There is deliberately NO hardware fingerprinting here - no motherboard or CPU serial, no
/// WMI. A RAM upgrade must not lock out a paying customer, this project has refused
/// fingerprinting elsewhere, and it is forgeable anyway so it would only stop the lazy.
///
/// The private key is wrapped with DPAPI at MACHINE scope, so copying %ProgramData% to another
/// computer does not carry the identity with it: the other machine cannot decrypt it. Machine
/// scope rather than user scope because the service runs as LocalSystem and the file has to be
/// readable by whatever account the service happens to run under.
/// </summary>
internal sealed class DeviceIdentity : IDisposable
{
    private const string FileName = "device.key";

    /// <summary>
    /// Extra entropy mixed into the DPAPI wrapping.
    ///
    /// Be honest about what this is worth: it is a constant in a binary that is intended to be
    /// published, so it is NOT a secret and it stops nobody who has looked at the source. What
    /// it does buy is that a blob produced here cannot be unprotected by a process that does not
    /// know it came from this application - which is exactly the shape of the generic credential
    /// stealers that walk a disk calling CryptUnprotectData on everything they find. That is a
    /// real category of attacker, and this is a one-line answer to it. It is not a defence
    /// against anything targeted.
    /// </summary>
    private static readonly byte[] Entropy = "GamePingBooster.DeviceIdentity.v1"u8.ToArray();

    private readonly ECDsa _key;

    /// <summary>The signing key. Used to sign the v3 handshake; never leaves this process.</summary>
    public ECDsa Key => _key;

    /// <summary>The device's name: 65 bytes, uncompressed P-256, 0x04 || X || Y.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The same thing as lowercase hex, which is how the licence server sees it.</summary>
    public string PublicKeyHex { get; }

    private DeviceIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = GpbCrypto.ExportPublicKey(key);
        PublicKeyHex = Convert.ToHexStringLower(PublicKey);
    }

    public void Dispose() => _key.Dispose();

    /// <summary>
    /// Opens a profile the licence server sealed to this machine.
    ///
    /// The private scalar is exported, used and zeroed inside this method rather than handed to
    /// the caller. TunnelEngine has no business holding it, and a scalar passed around is a
    /// scalar that ends up logged.
    /// </summary>
    /// <exception cref="CryptographicException">Not sealed to this device, or malformed.</exception>
    public byte[] OpenSealedProfile(ReadOnlySpan<byte> envelope)
    {
        var scalar = ExportScalar(_key);
        try
        {
            return ProfileEnvelope.Open(envelope, scalar);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>
    /// Loads the keypair, generating and saving one the first time.
    /// </summary>
    public static DeviceIdentity LoadOrCreate(Action<string> log)
    {
        var path = Path.Combine(ServiceConfig.DefaultDirectory, FileName);

        if (File.Exists(path))
        {
            try
            {
                var blob = File.ReadAllBytes(path);
                var d = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
                try
                {
                    var key = GpbCrypto.ImportPrivateKey(d);
                    return new DeviceIdentity(key);
                }
                finally
                {
                    // The scalar is the whole secret. Do not leave a copy of it lying in a heap
                    // buffer waiting to be swapped to disk or picked up by a crash dump.
                    CryptographicOperations.ZeroMemory(d);
                }
            }
            catch (Exception ex)
            {
                // Reached when the file is corrupt, or when it was copied here from a different
                // machine - DPAPI at machine scope will not decrypt it, which is exactly the
                // property that makes copying %ProgramData% around useless.
                //
                // Minting a replacement is the only way forward: refusing to start would leave
                // the user with dead software for a reason they cannot act on. But it must not
                // happen QUIETLY. Every new keypair is a new device as far as the licence server
                // is concerned, and a machine that silently reminted one on every start would
                // eat the account's device limit and present as "you have too many devices" -
                // which points at the account and not at this file. So the old file is kept, and
                // this is loud.
                var kept = path + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".unreadable";
                try
                {
                    File.Move(path, kept);
                    log($"DEVICE KEY UNREADABLE: {ex.Message}");
                    log($"  The old file has been kept as {kept} and a NEW device identity was generated.");
                    log("  This machine now counts as a new device. If this repeats on every start, that");
                    log("  is a bug and not a licensing problem - say so when reporting it.");
                }
                catch (Exception moveEx)
                {
                    // Could not even move it aside. Say so and carry on: an in-memory key still
                    // works for this run, which beats not starting.
                    log($"DEVICE KEY UNREADABLE and could not be set aside: {ex.Message} / {moveEx.Message}");
                }
            }
        }

        var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new DeviceIdentity(fresh);

        if (Save(fresh, path, log))
        {
            log($"Device identity created: {identity.PublicKeyHex}");
        }
        else
        {
            // Not fatal on purpose. The key works for this run; it just will not survive a
            // restart, and the next start will register the machine as a different device.
            log("Device identity could NOT be saved. It will be regenerated on the next start.");
        }

        return identity;
    }

    /// <summary>Writes the DPAPI-wrapped private key, atomically. True when it is on disk.</summary>
    private static bool Save(ECDsa key, string path, Action<string> log)
    {
        var d = ExportScalar(key);
        try
        {
            var blob = ProtectedData.Protect(d, Entropy, DataProtectionScope.LocalMachine);

            Directory.CreateDirectory(ServiceConfig.DefaultDirectory);

            // Via a temporary file and a replace, for the same reason ServiceConfig.Save does
            // it: a half-written device key is a device that has to be registered again.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log($"Could not save the device key: {ex.Message}");
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
        }
    }

    /// <summary>The 32-byte private scalar, left-padded.</summary>
    private static byte[] ExportScalar(ECDsa key)
    {
        var p = key.ExportParameters(true);
        var d = p.D ?? throw new CryptographicException("the key has no private half");

        // Right-aligned into a fixed 32 bytes rather than copied to offset 0. GpbCrypto.
        // ExportPublicKey carries the same note for X and Y, and the reason is the same: a
        // scalar shorter than the field size turns up about one key in 256, and copying it to
        // the front would shift every byte and produce a key that is silently not the one that
        // was generated. Rare enough to pass every manual test and still be wrong.
        if (d.Length == GpbCrypto.PrivateKeyLen)
        {
            return d;
        }
        if (d.Length > GpbCrypto.PrivateKeyLen)
        {
            throw new CryptographicException($"a P-256 scalar cannot be {d.Length} bytes");
        }

        var padded = new byte[GpbCrypto.PrivateKeyLen];
        d.CopyTo(padded, GpbCrypto.PrivateKeyLen - d.Length);
        CryptographicOperations.ZeroMemory(d);
        return padded;
    }
}
