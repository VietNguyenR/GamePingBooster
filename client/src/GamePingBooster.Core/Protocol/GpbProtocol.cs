using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace GamePingBooster.Core.Protocol;

/// <summary>
/// C# mirror of the wire format described in docs/PROTOCOL.md.
/// Must match relay/internal/protocol/protocol.go byte for byte - change one, change both.
/// </summary>
public static class GpbProtocol
{
    public const byte Version = 2;

    public const byte TypeHandshakeReq = 0x1;
    public const byte TypeHandshakeResp = 0x2;
    public const byte TypeData = 0x3;
    public const byte TypePing = 0x4;
    public const byte TypePong = 0x5;
    public const byte TypeDisconnect = 0x6;

    // Grew from 49 to 57 in v2 with the addition of the client id.
    public const int HandshakeReqLen = 57;
    public const int HandshakeRespLen = 52;
    public const int DataHeaderLen = 9;
    public const int PingLen = 17;
    public const int DisconnectLen = 9;
    public const int MaxPacketLen = 2048;

    public const byte StatusOk = 0;
    public const byte StatusPoolFull = 1;
    public const byte StatusShutdown = 2;
    public const byte StatusVersionMismatch = 3;

    private static byte Header(byte msgType) => (byte)((Version << 4) | (msgType & 0x0f));

    public static (byte Version, byte Type) ParseHeader(byte b) => ((byte)(b >> 4), (byte)(b & 0x0f));

    /// <summary>
    /// Builds a HandshakeReq signed with HMAC-SHA256 using the PSK.
    ///
    /// <paramref name="clientId"/> is what lets a reconnecting client keep the inner address it
    /// already has, so a brief network drop does not force the routing table to be rebuilt. It
    /// sits inside the signed range, so it cannot be swapped in transit.
    /// </summary>
    public static byte[] BuildHandshakeReq(byte[] psk, ulong clientId, DateTimeOffset now)
    {
        var pkt = new byte[HandshakeReqLen];
        pkt[0] = Header(TypeHandshakeReq);
        RandomNumberGenerator.Fill(pkt.AsSpan(1, 8));
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(9, 8), (ulong)now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(17, 8), clientId);
        HMACSHA256.HashData(psk, pkt.AsSpan(0, 25)).CopyTo(pkt.AsSpan(25));
        return pkt;
    }

    /// <summary>Handshake result, once the HMAC has been verified.</summary>
    public readonly record struct HandshakeResult(
        byte Status,
        ulong SessionId,
        IPAddress ClientIp,
        IPAddress RelayIp,
        ushort Mtu);

    /// <summary>
    /// Decodes and authenticates a HandshakeResp. Returns false on a wrong length, a wrong
    /// version, or a bad HMAC - in which case none of the fields may be trusted.
    /// </summary>
    public static bool TryParseHandshakeResp(byte[] psk, ReadOnlySpan<byte> pkt, out HandshakeResult result)
    {
        result = default;
        if (pkt.Length != HandshakeRespLen) return false;

        var (version, type) = ParseHeader(pkt[0]);
        if (type != TypeHandshakeResp) return false;

        // A relay speaking a different version answers with OUR version in the header and
        // StatusVersionMismatch in the body, precisely so this parser can read it. Rejecting it
        // on the version check would turn a clear diagnosis back into a silent timeout.
        if (version != Version && pkt[1] != StatusVersionMismatch) return false;

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(psk, pkt[..20], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, pkt[20..])) return false;

        result = new HandshakeResult(
            Status: pkt[1],
            SessionId: BinaryPrimitives.ReadUInt64BigEndian(pkt.Slice(2, 8)),
            ClientIp: new IPAddress(pkt.Slice(10, 4).ToArray()),
            RelayIp: new IPAddress(pkt.Slice(14, 4).ToArray()),
            Mtu: BinaryPrimitives.ReadUInt16BigEndian(pkt.Slice(18, 2)));
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
