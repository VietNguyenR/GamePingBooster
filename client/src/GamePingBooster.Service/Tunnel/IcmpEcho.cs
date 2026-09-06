using System.Buffers.Binary;
using System.Net;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// An IPv4 ICMP echo request, built by hand, and the matching of its reply.
///
/// Windows' own <c>System.Net.NetworkInformation.Ping</c> is the right tool for measuring the
/// physical path and is what <see cref="LandmarkProbe"/> uses. It is no use for measuring
/// through the tunnel: it hands the packet to the operating system, which routes it, and at
/// relay-selection time there is no virtual adapter and no route to hand it to. The tunnel is
/// just a UDP socket carrying whole IP packets, so a probe that has to travel through it has to
/// be an IP packet we assemble ourselves.
///
/// Twenty-eight bytes plus payload, no options, no fragmentation. The relay's kernel NATs it
/// like any other packet from the tunnel subnet - conntrack rewrites the echo id on the way out
/// and restores it on the way back, so the id and sequence we chose are what returns to us.
/// </summary>
internal static class IcmpEcho
{
    public const int Ipv4HeaderLen = 20;
    public const int IcmpHeaderLen = 8;

    private const byte ProtocolIcmp = 1;
    private const byte TypeEchoRequest = 8;
    private const byte TypeEchoReply = 0;

    /// <summary>
    /// Writes an echo request into <paramref name="destination"/> and returns its length.
    ///
    /// The payload is filler and its only job is to make the packet a realistic size rather than
    /// a runt that some middlebox treats differently from game traffic.
    /// </summary>
    public static int Build(Span<byte> destination, IPAddress source, IPAddress target,
        ushort id, ushort sequence, int payloadLen = 32)
    {
        var total = Ipv4HeaderLen + IcmpHeaderLen + payloadLen;
        if (destination.Length < total) throw new ArgumentException("Buffer too small for the echo request.");
        destination[..total].Clear();

        var ip = destination[..Ipv4HeaderLen];
        ip[0] = 0x45;                                                    // IPv4, 5 words of header
        ip[1] = 0;                                                       // no DSCP, no ECN
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(ip[4..], id);              // identification
        BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0);               // no flags, no fragment
        ip[8] = 64;                                                      // TTL
        ip[9] = ProtocolIcmp;
        source.TryWriteBytes(ip[12..16], out _);
        target.TryWriteBytes(ip[16..20], out _);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Checksum(ip));

        var icmp = destination[Ipv4HeaderLen..total];
        icmp[0] = TypeEchoRequest;
        icmp[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[4..], id);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[6..], sequence);
        for (var i = 0; i < payloadLen; i++) icmp[IcmpHeaderLen + i] = (byte)('a' + i % 23);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Checksum(icmp));

        return total;
    }

    /// <summary>
    /// True when <paramref name="packet"/> is the echo reply we are waiting for.
    ///
    /// The source address is checked as well as the id and sequence. Without it a reply from any
    /// host - a router's time-exceeded, say, or an unrelated ping answered on the same tunnel -
    /// could be counted as this landmark's answer and put a wrong number into the comparison
    /// that decides which relay the player gets.
    /// </summary>
    public static bool IsReplyTo(ReadOnlySpan<byte> packet, IPAddress from, ushort id, ushort sequence)
    {
        if (packet.Length < Ipv4HeaderLen + IcmpHeaderLen) return false;
        if ((packet[0] >> 4) != 4) return false;

        var headerLen = (packet[0] & 0x0f) * 4;
        if (headerLen < Ipv4HeaderLen || packet.Length < headerLen + IcmpHeaderLen) return false;
        if (packet[9] != ProtocolIcmp) return false;

        Span<byte> expected = stackalloc byte[4];
        if (!from.TryWriteBytes(expected, out _)) return false;
        if (!packet.Slice(12, 4).SequenceEqual(expected)) return false;

        var icmp = packet[headerLen..];
        return icmp[0] == TypeEchoReply
            && BinaryPrimitives.ReadUInt16BigEndian(icmp[4..]) == id
            && BinaryPrimitives.ReadUInt16BigEndian(icmp[6..]) == sequence;
    }

    /// <summary>The internet checksum: one's complement of the one's complement sum of 16-bit words.</summary>
    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }
}
