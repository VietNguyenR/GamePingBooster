using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace GamePingBooster.Core.Protocol;

/// <summary>
/// C# mirror of the wire format described in docs/PROTOCOL-v3.md.
/// Must match relay/internal/protocol/protocol.go byte for byte - change one, change both.
/// </summary>
public static class GpbProtocol
{
    public const byte Version = 3;

    public const byte TypeHandshakeReq = 0x1;
    public const byte TypeHandshakeResp = 0x2;
    public const byte TypeData = 0x3;
    public const byte TypePing = 0x4;
    public const byte TypePong = 0x5;
    public const byte TypeDisconnect = 0x6;

    /// <summary>
    /// RESERVED and never sent or accepted. Data is deliberately plaintext - see
    /// docs/PROTOCOL-v3.md. Holding the number means encryption can be added later beside the
    /// plaintext path instead of forcing a second handshake redesign.
    /// </summary>
    public const byte TypeDataEncrypted = 0x7;

    /// <summary>Self-hosted mode: one shared key, as in v1 and v2.</summary>
    public const byte AuthModePsk = 0;

    /// <summary>Commercial mode: a licence token the relay verifies offline.</summary>
    public const byte AuthModeToken = 1;

    // v2's 57 bytes plus the auth-mode byte.
    public const int HandshakeReqPskLen = 58;
    // Carries the licence token and a device signature instead of an HMAC.
    public const int HandshakeReqTokenLen = 240;

    // v2's 52 plus the mode byte and the nonce echo.
    public const int HandshakeRespPskLen = 60;
    // Swaps the 32-byte HMAC for a 64-byte relay signature.
    public const int HandshakeRespTokenLen = 92;

    /// <summary>
    /// The v2 layout, kept ONLY so a version-mismatch refusal from an older relay can still be
    /// read. Nothing else may use it.
    /// </summary>
    public const int HandshakeRespV2Len = 52;

    public const int TokenLen = 150;
    public const int NonceLen = 8;

    /// <summary>
    /// How far this machine's clock may sit from the relay's before a handshake is refused.
    /// Mirrors HandshakeSkew in relay/internal/protocol/protocol.go; the two must agree.
    ///
    /// This side never checks it - the relay does, and it drops a skewed handshake in silence,
    /// on every relay at once, which looks exactly like a blocked UDP port. The constant is here
    /// so the app can measure its own clock against the licence server and say so BEFORE anybody
    /// spends an afternoon on the network. See LicenceClient.ClockSkew.
    /// </summary>
    public static readonly TimeSpan HandshakeSkew = TimeSpan.FromSeconds(120);

    public const int DataHeaderLen = 9;
    public const int PingLen = 17;
    public const int DisconnectLen = 9;
    public const int MaxPacketLen = 2048;

    public const byte StatusOk = 0;
    public const byte StatusPoolFull = 1;
    public const byte StatusShutdown = 2;
    public const byte StatusVersionMismatch = 3;
    public const byte StatusCredentialExpired = 4;
    public const byte StatusCredentialRevoked = 5;

    /// <summary>
    /// The licence verified, but this relay is reserved for a higher plan than the token carries.
    ///
    /// Distinct from an expired credential on purpose: nothing is wrong with the subscription and
    /// signing in again will not help. The relay is simply not one this plan reaches, and the
    /// only actions that change that are upgrading or picking a different relay.
    /// </summary>
    public const byte StatusTierTooLow = 6;

    // Field offsets shared by both request layouts. Everything after the header moved by one
    // when the auth-mode byte was inserted, which is the single easiest thing to get wrong here.
    private const int ReqOffMode = 1;
    private const int ReqOffNonce = 2;
    private const int ReqOffTime = 10;
    private const int ReqOffClientId = 18;
    private const int ReqOffAuth = 26;
    private const int ReqTokenSigOff = ReqOffAuth + TokenLen; // 176

    // Response offsets, shared by both layouts.
    private const int RespOffNonce = 20;
    private const int RespOffAuth = 28;

    private static byte Header(byte msgType) => (byte)((Version << 4) | (msgType & 0x0f));

    public static (byte Version, byte Type) ParseHeader(byte b) => ((byte)(b >> 4), (byte)(b & 0x0f));

    /// <summary>
    /// Builds a PSK-mode HandshakeReq signed with HMAC-SHA256.
    ///
    /// <paramref name="nonce"/> comes back out because the answer echoes it, and checking that
    /// echo is what stops a captured response being replayed at a client that is mid-handshake.
    /// Keep it until the answer arrives.
    ///
    /// <paramref name="clientId"/> is what lets a reconnecting client keep the inner address it
    /// already has, so a brief network drop does not force the routing table to be rebuilt. It
    /// sits inside the signed range, so it cannot be swapped in transit.
    /// </summary>
    public static byte[] BuildHandshakeReq(byte[] psk, ulong clientId, DateTimeOffset now, out byte[] nonce)
    {
        var pkt = new byte[HandshakeReqPskLen];
        pkt[0] = Header(TypeHandshakeReq);
        pkt[ReqOffMode] = AuthModePsk;
        RandomNumberGenerator.Fill(pkt.AsSpan(ReqOffNonce, NonceLen));
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(ReqOffTime, 8), (ulong)now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(ReqOffClientId, 8), clientId);
        HMACSHA256.HashData(psk, pkt.AsSpan(0, ReqOffAuth)).CopyTo(pkt.AsSpan(ReqOffAuth));

        nonce = pkt.AsSpan(ReqOffNonce, NonceLen).ToArray();
        return pkt;
    }

    /// <summary>
    /// Builds a token-mode HandshakeReq, signed with the device key.
    ///
    /// The device public key is not a field: it is inside the token, where the licence server put
    /// it. That is what stops a stolen token being useful on its own - whoever presents it must
    /// also hold the matching private key, which never leaves the machine it was made on.
    /// </summary>
    public static byte[] BuildHandshakeReqToken(ECDsa deviceKey, ReadOnlySpan<byte> token,
        ulong clientId, DateTimeOffset now, out byte[] nonce)
    {
        if (token.Length != TokenLen)
        {
            throw new ArgumentException($"a licence token is {TokenLen} bytes", nameof(token));
        }

        var pkt = new byte[HandshakeReqTokenLen];
        pkt[0] = Header(TypeHandshakeReq);
        pkt[ReqOffMode] = AuthModeToken;
        RandomNumberGenerator.Fill(pkt.AsSpan(ReqOffNonce, NonceLen));
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(ReqOffTime, 8), (ulong)now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(ReqOffClientId, 8), clientId);
        token.CopyTo(pkt.AsSpan(ReqOffAuth, TokenLen));

        GpbCrypto.Sign(deviceKey, pkt.AsSpan(0, ReqTokenSigOff)).CopyTo(pkt.AsSpan(ReqTokenSigOff));

        nonce = pkt.AsSpan(ReqOffNonce, NonceLen).ToArray();
        return pkt;
    }

    /// <summary>Handshake result, once the HMAC has been verified.</summary>
    public readonly record struct HandshakeResult(
        byte Status,
        ulong SessionId,
        IPAddress ClientIp,
        IPAddress RelayIp,
        ushort Mtu);

    private static HandshakeResult ReadHandshakeResp(ReadOnlySpan<byte> pkt) => new(
        Status: pkt[1],
        SessionId: BinaryPrimitives.ReadUInt64BigEndian(pkt.Slice(2, 8)),
        ClientIp: new IPAddress(pkt.Slice(10, 4).ToArray()),
        RelayIp: new IPAddress(pkt.Slice(14, 4).ToArray()),
        Mtu: BinaryPrimitives.ReadUInt16BigEndian(pkt.Slice(18, 2)));

    /// <summary>
    /// Decodes and authenticates a PSK-mode HandshakeResp. Returns false on a wrong length, a
    /// wrong version, a bad HMAC, or a nonce that does not match the one that was sent - in any
    /// of which cases none of the fields may be trusted.
    ///
    /// <paramref name="sentNonce"/> is the value BuildHandshakeReq handed back. Checking the echo
    /// is what stops a captured answer being replayed at a client that is mid-handshake: without
    /// it the client adopts a session id the relay has already forgotten and the tunnel comes up
    /// carrying nothing until the idle timeout. v2 had exactly this hole.
    /// </summary>
    public static bool TryParseHandshakeResp(byte[] psk, ReadOnlySpan<byte> pkt,
        ReadOnlySpan<byte> sentNonce, out HandshakeResult result)
    {
        result = default;

        // A relay one version behind answers with a v2-layout refusal carrying OUR version in
        // the header, precisely so this parser can read it. It is 52 bytes, not 60, so the
        // length check has to allow for it or a clear diagnosis turns back into a timeout - and
        // that is the whole reason that message exists.
        if (pkt.Length == HandshakeRespV2Len)
        {
            if (ParseHeader(pkt[0]).Type != TypeHandshakeResp) return false;
            if (pkt[1] != StatusVersionMismatch) return false;

            Span<byte> legacy = stackalloc byte[32];
            HMACSHA256.HashData(psk, pkt[..20], legacy);
            if (!CryptographicOperations.FixedTimeEquals(legacy, pkt[20..])) return false;

            result = ReadHandshakeResp(pkt);
            return true;
        }

        if (pkt.Length != HandshakeRespPskLen) return false;

        var (version, type) = ParseHeader(pkt[0]);
        if (type != TypeHandshakeResp) return false;
        if (version != Version && pkt[1] != StatusVersionMismatch) return false;

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(psk, pkt[..RespOffAuth], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, pkt[RespOffAuth..])) return false;

        if (!CryptographicOperations.FixedTimeEquals(pkt.Slice(RespOffNonce, NonceLen), sentNonce)) return false;

        result = ReadHandshakeResp(pkt);
        return true;
    }

    /// <summary>
    /// Decodes and authenticates a token-mode HandshakeResp against the relay's own public key,
    /// which the client got from the profile.
    ///
    /// In token mode there is no shared secret, so the relay signs with a key of its own. This is
    /// new in v3: under v2 a client could only tell a real relay from a forged answer because
    /// both sides happened to hold the same key.
    /// </summary>
    public static bool TryParseHandshakeRespToken(ECDsa relayKey, ReadOnlySpan<byte> pkt,
        ReadOnlySpan<byte> sentNonce, out HandshakeResult result)
    {
        result = default;
        if (pkt.Length != HandshakeRespTokenLen) return false;

        var (version, type) = ParseHeader(pkt[0]);
        if (type != TypeHandshakeResp) return false;
        if (version != Version) return false;

        if (!GpbCrypto.Verify(relayKey, pkt[..RespOffAuth], pkt[RespOffAuth..])) return false;
        if (!CryptographicOperations.FixedTimeEquals(pkt.Slice(RespOffNonce, NonceLen), sentNonce)) return false;

        result = ReadHandshakeResp(pkt);
        return true;
    }

    /// <summary>
    /// Writes the Data header plus the IP packet into <paramref name="destination"/>.
    /// Allocates nothing - this is the hot path, every game packet goes through it.
    /// </summary>
    public static int WriteData(Span<byte> destination, ulong sessionId, ReadOnlySpan<byte> ipPacket)
    {
        destination[0] = Header(TypeData);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(1, 8), sessionId);
        ipPacket.CopyTo(destination[DataHeaderLen..]);
        return DataHeaderLen + ipPacket.Length;
    }

    /// <summary>Splits the IP payload out of a Data message. The result aliases the buffer.</summary>
    public static bool TryReadData(ReadOnlySpan<byte> pkt, out ulong sessionId, out ReadOnlySpan<byte> ipPacket)
    {
        sessionId = 0;
        ipPacket = default;
        if (pkt.Length <= DataHeaderLen) return false;

        sessionId = BinaryPrimitives.ReadUInt64BigEndian(pkt.Slice(1, 8));
        var payload = pkt[DataHeaderLen..];
        if ((payload[0] >> 4) != 4) return false; // IPv4 only

        ipPacket = payload;
        return true;
    }

    public static byte[] BuildPing(ulong sessionId, ulong stamp) => BuildPingLike(TypePing, sessionId, stamp);

    private static byte[] BuildPingLike(byte type, ulong sessionId, ulong stamp)
    {
        var pkt = new byte[PingLen];
        pkt[0] = Header(type);
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(1, 8), sessionId);
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(9, 8), stamp);
        return pkt;
    }

    public static bool TryReadPong(ReadOnlySpan<byte> pkt, out ulong sessionId, out ulong stamp)
    {
        sessionId = 0;
        stamp = 0;
        if (pkt.Length != PingLen) return false;
        sessionId = BinaryPrimitives.ReadUInt64BigEndian(pkt.Slice(1, 8));
        stamp = BinaryPrimitives.ReadUInt64BigEndian(pkt.Slice(9, 8));
        return true;
    }

    public static byte[] BuildDisconnect(ulong sessionId)
    {
        var pkt = new byte[DisconnectLen];
        pkt[0] = Header(TypeDisconnect);
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(1, 8), sessionId);
        return pkt;
    }
}
