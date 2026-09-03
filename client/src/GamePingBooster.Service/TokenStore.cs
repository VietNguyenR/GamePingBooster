using System.Buffers.Binary;
using System.Security.Cryptography;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.Service;

/// <summary>
/// The licence token on disk: 150 opaque bytes, wrapped with DPAPI at machine scope.
///
/// The token is minted by the licence server, is meaningless to this client, and is verified by
/// the relay against a public key the client never sees. Nothing here validates it - it cannot,
/// because the signing key is not on this machine - so everything below reads it, never trusts
/// it. Reading the expiry is for deciding when to ask for a new one and what to tell the user;
/// the relay is the thing that actually decides whether it is good.
///
/// The contrast with <see cref="DeviceIdentity"/> is the point, and the two must not be treated
/// alike:
///
///   device key   irreplaceable. Losing it means the licence server registers this machine
///                again and the account burns a device slot, so an unreadable file is kept and
///                shouted about.
///   token        disposable. It expires in a day anyway. An unreadable one is simply deleted;
///                the UI signs in and gets another. Keeping a corrupt token around, or refusing
///                to start over one, would turn a self-healing situation into a support case.
/// </summary>
internal static class TokenStore
{
    private const string FileName = "token";

    /// <summary>See the note on DeviceIdentity.Entropy: a domain separator, not a secret.</summary>
    private static readonly byte[] Entropy = "GamePingBooster.LicenceToken.v1"u8.ToArray();

    private static string Path => System.IO.Path.Combine(ServiceConfig.DefaultDirectory, FileName);

    /// <summary>The stored token, or null when there is none or it could not be read.</summary>
    public static byte[]? Load(Action<string> log)
    {
        var path = Path;
        if (!File.Exists(path)) return null;

        try
        {
            var blob = File.ReadAllBytes(path);
            var token = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
            if (token.Length != GpbProtocol.TokenLen)
            {
                // Wrong length means a different format, not a corrupt file: a token from a
                // future version of the licence server would land here. Discard it rather than
                // sending 150 bytes of something else to a relay.
                log($"The stored licence token is {token.Length} bytes, expected {GpbProtocol.TokenLen}. Discarding it.");
                Clear(log);
                return null;
            }
            return token;
        }
        catch (Exception ex)
        {
            // Corrupt, or wrapped on another machine. Either way it is worthless here, and a
            // replacement is one sign-in away.
            log($"The stored licence token could not be read ({ex.Message}). Discarding it; sign in again.");
            Clear(log);
            return null;
        }
    }

    /// <summary>Replaces the stored token. Returns false when it could not be written.</summary>
    public static bool Save(ReadOnlySpan<byte> token, Action<string> log)
    {
        if (token.Length != GpbProtocol.TokenLen)
        {
            log($"Refusing to store a {token.Length}-byte licence token; it must be {GpbProtocol.TokenLen}.");
            return false;
        }

        try
        {
            var blob = ProtectedData.Protect(token.ToArray(), Entropy, DataProtectionScope.LocalMachine);
            Directory.CreateDirectory(ServiceConfig.DefaultDirectory);

            // Temporary file then replace, as everything else here does: a half-written token is
            // a client that cannot connect until it signs in again.
            var tmp = Path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log($"Could not store the licence token: {ex.Message}");
            return false;
        }
    }

    public static void Clear(Action<string> log)
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (Exception ex)
        {
            log($"Could not remove the stored licence token: {ex.Message}");
        }
    }

    /// <summary>
    /// The expiry the token carries, read straight out of bytes 74..82.
    ///
    /// Read, not trusted. A client that edited this field would still be refused by the relay,
    /// which checks the signature over the whole token. It is here so the service knows when to
    /// ask for a new one - the refresh happens at 50% of remaining life, so a handshake never
    /// carries a nearly expired token - and so the UI can say something useful.
    ///
    /// The layout is in docs/PROTOCOL-v3.md and is the same eight bytes VerifyToken reads on the
    /// relay. If it ever moves, it moves in both places.
    /// </summary>
    public static DateTimeOffset ExpiryOf(ReadOnlySpan<byte> token)
    {
        if (token.Length != GpbProtocol.TokenLen)
        {
            throw new ArgumentException($"a licence token is {GpbProtocol.TokenLen} bytes", nameof(token));
        }
        var unix = BinaryPrimitives.ReadUInt64BigEndian(token.Slice(TokenOffExpiry, 8));
        return DateTimeOffset.FromUnixTimeSeconds((long)unix);
    }

    /// <summary>Offset of the expiry field inside a token. See docs/PROTOCOL-v3.md.</summary>
    private const int TokenOffExpiry = 74;
}
