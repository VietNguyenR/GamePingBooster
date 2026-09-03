using System.Security.Cryptography;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// How one tunnel authenticates itself to one relay: either the shared key, or a licence token
/// signed with this machine's device key.
///
/// It is a type rather than a pair of nullable parameters on TunnelClient because the two modes
/// need different things and mixing them is meaningless - a token with no relay public key
/// cannot verify the answer, a PSK with a device key ignores it. Making the invalid combinations
/// unrepresentable is cheaper than checking for them at the point of use, which is inside a
/// retry loop.
///
/// The mode is decided PER RELAY, not per client. A profile relay that publishes a public key
/// can be reached with a token; an endpoint a self-hoster typed into the settings screen cannot,
/// because there is no key to check its answer against, and it should not be - self-hosted
/// installations must keep working exactly as they did.
/// </summary>
internal sealed class TunnelAuth : IDisposable
{
    private readonly byte[]? _psk;
    private readonly byte[]? _token;
    private readonly ECDsa? _deviceKey;
    private readonly ECDsa? _relayKey;

    private TunnelAuth(byte[]? psk, byte[]? token, ECDsa? deviceKey, ECDsa? relayKey)
    {
        _psk = psk;
        _token = token;
        _deviceKey = deviceKey;
        _relayKey = relayKey;
    }

    /// <summary>Self-hosted: both ends hold the same secret.</summary>
    public static TunnelAuth FromPsk(byte[] psk) => new(psk, null, null, null);

    /// <summary>
    /// Licensed: a token signed by the licence server, presented with proof that this machine
    /// holds the device key the token names.
    /// </summary>
    /// <param name="relayPublicKeyHex">
    /// The relay's own public key from the profile. Required: without it the client cannot tell
    /// a real relay's answer from anything else that happens to arrive.
    /// </param>
    public static TunnelAuth FromToken(byte[] token, ECDsa deviceKey, string relayPublicKeyHex)
    {
        if (token.Length != GpbProtocol.TokenLen)
        {
            throw new ArgumentException($"a licence token is {GpbProtocol.TokenLen} bytes", nameof(token));
        }
        // ImportPublicKey validates the point is actually on P-256. A malformed key in a profile
        // must fail here, loudly, and not at the moment a signature silently fails to verify.
        var relayKey = GpbCrypto.ImportPublicKey(Convert.FromHexString(relayPublicKeyHex));
        return new TunnelAuth(null, token, deviceKey, relayKey);
    }

    public bool IsToken => _token is not null;

    /// <summary>A word for the log, so a session's mode is never a guess.</summary>
    public string Describe => IsToken ? "licence token" : "pre-shared key";

    public byte[] BuildRequest(ulong clientId, DateTimeOffset now, out byte[] nonce) =>
        IsToken
            ? GpbProtocol.BuildHandshakeReqToken(_deviceKey!, _token, clientId, now, out nonce)
            : GpbProtocol.BuildHandshakeReq(_psk!, clientId, now, out nonce);

    public bool TryParseResponse(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> sentNonce,
        out GpbProtocol.HandshakeResult result) =>
        IsToken
            ? GpbProtocol.TryParseHandshakeRespToken(_relayKey!, packet, sentNonce, out result)
            : GpbProtocol.TryParseHandshakeResp(_psk!, packet, sentNonce, out result);

    /// <summary>
    /// Only the relay key is owned here. The device key belongs to DeviceIdentity and outlives
    /// every tunnel - disposing it would break the next connect, and the one after a failover.
    /// </summary>
    public void Dispose() => _relayKey?.Dispose();
}
