using System.Buffers.Binary;

namespace GamePingBooster.PathCheck;

/// <summary>
/// Builds IPv4 packets with correct checksums, and verifies them, by FULL recomputation.
///
/// Deliberately written from the RFCs and sharing nothing with InnerNat: InnerNat adjusts checksums
/// incrementally, this sums every word. If both were wrong the same way the checks would pass, so they are
/// not allowed to be the same code.
/// </summary>
internal static class Packets
{
    public const byte Icmp = 1, Tcp = 6, Udp = 17;

    // ------------------------------------------------------------ one's complement

    public static uint Sum(ReadOnlySpan<byte> data, uint sum = 0)
    {
        var i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        return sum;
    }

    public static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)sum;
    }

    public static ushort Finish(uint sum) => (ushort)~Fold(sum);

    public static uint Pseudo(uint src, uint dst, byte protocol, int length) =>
        (src >> 16) + (src & 0xFFFF) + (dst >> 16) + (dst & 0xFFFF) + protocol + (uint)length;

    // ------------------------------------------------------------ building

    /// <summary>An IPv4 packet around <paramref name="payload"/>, header checksum filled in.</summary>
    public static byte[] Ip(uint src, uint dst, byte protocol, ReadOnlySpan<byte> payload, Random rng,
        int optionWords = 0, int fragmentOffset8 = 0, bool moreFragments = false, byte ttl = 64)
    {
        var ihl = 20 + optionWords * 4;
        var packet = new byte[ihl + payload.Length];
        packet[0] = (byte)(0x40 | (ihl / 4));
        packet[1] = (byte)rng.Next(256);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)rng.Next(65536));
        var flags = (moreFragments ? 0x2000 : 0) | (fragmentOffset8 & 0x1FFF);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)flags);
        packet[8] = ttl;
        packet[9] = protocol;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), src);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), dst);
        for (var i = 20; i < ihl; i++) packet[i] = (byte)rng.Next(256);
        payload.CopyTo(packet.AsSpan(ihl));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Finish(Sum(packet.AsSpan(0, ihl))));
        return packet;
    }

    /// <summary>A UDP datagram, header and payload, with its checksum for these addresses - or none.</summary>
    public static byte[] UdpDatagram(uint src, uint dst, ushort sport, ushort dport, ReadOnlySpan<byte> payload, bool noChecksum = false)
    {
        var udp = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(0), sport);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), dport);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4), (ushort)udp.Length);
        payload.CopyTo(udp.AsSpan(8));
        if (!noChecksum) BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(6), UdpChecksum(src, dst, udp));
        return udp;
    }

    /// <summary>The UDP checksum a datagram should carry: zero computed is sent as 0xFFFF.</summary>
    public static ushort UdpChecksum(uint src, uint dst, ReadOnlySpan<byte> udp)
    {
        var copy = udp.ToArray();
        copy[6] = copy[7] = 0;
        var sum = Finish(Sum(copy, Pseudo(src, dst, Udp, copy.Length)));
        return sum == 0 ? (ushort)0xFFFF : sum;
    }

    public static byte[] TcpSegment(uint src, uint dst, Random rng, ReadOnlySpan<byte> payload)
    {
        var tcp = new byte[20 + payload.Length];
        rng.NextBytes(tcp.AsSpan(0, 12));
        tcp[12] = 0x50;
        tcp[13] = (byte)rng.Next(256);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(14), (ushort)rng.Next(65536));
        payload.CopyTo(tcp.AsSpan(20));
        tcp[16] = tcp[17] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(16), Finish(Sum(tcp, Pseudo(src, dst, Tcp, tcp.Length))));
        return tcp;
    }

    public static byte[] IcmpEcho(byte type, ushort id, ushort sequence, ReadOnlySpan<byte> data)
    {
        var icmp = new byte[8 + data.Length];
        icmp[0] = type;
        BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(6), sequence);
        data.CopyTo(icmp.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(2), Finish(Sum(icmp)));
        return icmp;
    }

    /// <summary>An ICMP error quoting the first <paramref name="quoteLength"/> bytes of <paramref name="about"/>.</summary>
    public static byte[] IcmpError(byte type, byte code, ReadOnlySpan<byte> about, int quoteLength)
    {
        quoteLength = Math.Min(quoteLength, about.Length);
        var icmp = new byte[8 + quoteLength];
        icmp[0] = type;
        icmp[1] = code;
        if (type == 3 && code == 4) BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(6), 1280);
        about[..quoteLength].CopyTo(icmp.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(2), Finish(Sum(icmp)));
        return icmp;
    }

    // ------------------------------------------------------------ reading and verifying

    public static int Ihl(ReadOnlySpan<byte> ip) => (ip[0] & 0x0F) * 4;
    public static uint Src(ReadOnlySpan<byte> ip) => BinaryPrimitives.ReadUInt32BigEndian(ip[12..]);
    public static uint Dst(ReadOnlySpan<byte> ip) => BinaryPrimitives.ReadUInt32BigEndian(ip[16..]);

    public static bool IpHeaderOk(ReadOnlySpan<byte> ip) => Fold(Sum(ip[..Ihl(ip)])) == 0xFFFF;

    /// <summary>A whole, unfragmented UDP packet: no checksum, or one that verifies.</summary>
    public static bool UdpOk(ReadOnlySpan<byte> ip)
    {
        var udp = ip[Ihl(ip)..];
        if (BinaryPrimitives.ReadUInt16BigEndian(udp[6..]) == 0) return true;
        return Fold(Sum(udp, Pseudo(Src(ip), Dst(ip), Udp, udp.Length))) == 0xFFFF;
    }

    public static bool TcpOk(ReadOnlySpan<byte> ip)
    {
        var tcp = ip[Ihl(ip)..];
        return Fold(Sum(tcp, Pseudo(Src(ip), Dst(ip), Tcp, tcp.Length))) == 0xFFFF;
    }

    public static bool IcmpOk(ReadOnlySpan<byte> ip) => Fold(Sum(ip[Ihl(ip)..])) == 0xFFFF;

    public static uint Addr(int a, int b, int c, int d) => (uint)(a << 24 | b << 16 | c << 8 | d);

    public static string Show(uint address) => $"{address >> 24}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";

    /// <summary>A public-looking address - never 10/8, so it can never be taken for an inner one.</summary>
    public static uint RandomServer(Random rng) => Addr(rng.Next(11, 223), rng.Next(256), rng.Next(256), rng.Next(1, 255));

    public static byte[] RandomBytes(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }
}
