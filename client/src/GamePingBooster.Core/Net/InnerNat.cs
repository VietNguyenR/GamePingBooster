using System.Buffers.Binary;

namespace GamePingBooster.Core.Net;

/// <summary>
/// Rewrites the one inner address of a packet crossing a tunnel whose relay handed out a different
/// address from the one on the virtual adapter. See docs/MULTI-TUNNEL.md, section 5.2.
///
/// Every relay gives each session an address from its own pool and drops a packet whose source is not
/// that address (relay/internal/server/server.go, handleData). The adapter carries one address. So a
/// packet leaving through a second tunnel must carry that tunnel's address, and its answer must reach
/// Windows with the adapter's - the same one-address NAT a home router does, and nothing more.
///
/// Checksums are adjusted incrementally (RFC 1624, equation 3), never recomputed. That keeps the cost
/// independent of the packet's size, and it is also the only correct way for a first fragment: its UDP
/// checksum covers a datagram this side never sees whole.
///
/// What is touched:
///   - the IPv4 source (uplink) or destination (downlink), and the header checksum, options included;
///   - the UDP or TCP checksum of a packet whose L4 header is in it (the pseudo-header holds both
///     addresses). A UDP checksum of zero means "none" and stays zero; one that comes out as zero is
///     written 0xFFFF (RFC 768). A non-first fragment has no L4 header and only its IP header changes;
///   - in an ICMP error (3, 4, 5, 11, 12), the quoted packet's address on the same side - source on the
///     downlink, destination on the uplink - with the quoted header's checksum, the quoted UDP or TCP
///     checksum when its bytes are there, and the ICMP checksum for every word that moved. Without this
///     an error the relay's kernel sent back about our packet quotes an address Windows never used, and
///     Windows drops it: path-MTU discovery and "port unreachable" both arrive this way.
///
/// A packet that cannot be parsed is refused whole. Nothing is ever forwarded half rewritten.
///
/// Pure and allocation-free: runs on the packet path, once per packet, for tunnels that need it only.
/// </summary>
public static class InnerNat
{
    public enum Outcome
    {
        /// <summary>The address was found and rewritten, with every checksum that covers it.</summary>
        Rewritten,

        /// <summary>The address on that side is not the one to rewrite. Nothing was changed.</summary>
        NotOurs,

        /// <summary>Not a well-formed IPv4 packet, or too short for a header it declares. Nothing was changed.</summary>
        Malformed,
    }

    private const byte ProtoIcmp = 1;
    private const byte ProtoTcp = 6;
    private const byte ProtoUdp = 17;

    /// <summary>Uplink: the source address <paramref name="from"/> becomes <paramref name="to"/>.</summary>
    public static Outcome RewriteSource(Span<byte> packet, uint from, uint to) => Rewrite(packet, from, to, source: true);

    /// <summary>Downlink: the destination address <paramref name="from"/> becomes <paramref name="to"/>.</summary>
    public static Outcome RewriteDestination(Span<byte> packet, uint from, uint to) => Rewrite(packet, from, to, source: false);

    private static Outcome Rewrite(Span<byte> packet, uint from, uint to, bool source)
    {
        if (!TryHeader(packet, out var ihl, out var total)) return Outcome.Malformed;

        var addressAt = source ? 12 : 16;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[addressAt..]) != from) return Outcome.NotOurs;
        if (from == to) return Outcome.Rewritten;

        var ip = packet[..total];
        var protocol = ip[9];
        var firstFragment = (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x1FFF) == 0;

        // Every check before the first write: a refusal leaves the packet exactly as it came.
        var l4 = ip[ihl..];
        var quoted = default(QuotedPacket);
        if (firstFragment)
        {
            switch (protocol)
            {
                case ProtoUdp when l4.Length < 8:
                case ProtoTcp when l4.Length < 20:
                    return Outcome.Malformed;
                case ProtoIcmp when l4.Length < 8:
                    return Outcome.Malformed;
                case ProtoIcmp when IsError(l4[0]):
                    // The quoted packet is ours on the OTHER side: we sent it (uplink source is ours, so it
                    // came back quoting our address as its source - the downlink case), or it was sent to us
                    // (the uplink case, an error Windows raises about a packet it received).
                    if (!TryQuoted(l4, quotedSource: !source, from, out quoted)) return Outcome.Malformed;
                    break;
            }
        }

        // The IP header: the address and its checksum.
        BinaryPrimitives.WriteUInt32BigEndian(ip[addressAt..], to);
        WriteChecksum(ip[10..], Adjust32(ReadChecksum(ip[10..]), from, to));

        if (!firstFragment) return Outcome.Rewritten;

        switch (protocol)
        {
            case ProtoUdp:
            {
                var sum = ReadChecksum(l4[6..]);
                if (sum != 0) WriteChecksum(l4[6..], NonZero(Adjust32(sum, from, to)));
                break;
            }
            case ProtoTcp:
                WriteChecksum(l4[16..], Adjust32(ReadChecksum(l4[16..]), from, to));
                break;
            case ProtoIcmp when quoted.Present:
                RewriteQuoted(l4, quoted, from, to);
                break;
        }
        return Outcome.Rewritten;
    }

    /// <summary>
    /// Version 4, a header length that fits, and a total length inside the buffer. The total length is what
    /// bounds everything after: bytes past it are not the packet's.
    /// </summary>
    private static bool TryHeader(ReadOnlySpan<byte> packet, out int ihl, out int total)
    {
        ihl = total = 0;
        if (packet.Length < 20 || packet[0] >> 4 != 4) return false;
        ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20) return false;
        total = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        return total >= ihl && total <= packet.Length;
    }

    /// <summary>Destination unreachable, source quench, redirect, time exceeded, parameter problem.</summary>
    private static bool IsError(byte type) => type is 3 or 4 or 5 or 11 or 12;

    private readonly struct QuotedPacket
    {
        public bool Present { get; init; }
        /// <summary>Offset of the quoted IP header inside the ICMP message.</summary>
        public int IpAt { get; init; }
        /// <summary>Offset of the quoted address inside the ICMP message.</summary>
        public int AddressAt { get; init; }
        /// <summary>Offset of the quoted L4 checksum inside the ICMP message, or -1 when it is not there or means nothing.</summary>
        public int L4ChecksumAt { get; init; }
        public bool L4IsUdp { get; init; }
    }

    /// <summary>
    /// Finds the quoted packet inside an ICMP error. An error that quotes somebody else's address is not
    /// ours to touch and its quote is left alone; one that quotes ours but cannot be parsed is refused.
    /// </summary>
    private static bool TryQuoted(ReadOnlySpan<byte> icmp, bool quotedSource, uint ours, out QuotedPacket quoted)
    {
        quoted = default;
        var inner = icmp[8..];
        if (inner.Length < 20 || inner[0] >> 4 != 4) return true;   // nothing parseable quoted: outer only

        var addressAt = 8 + (quotedSource ? 12 : 16);
        if (BinaryPrimitives.ReadUInt32BigEndian(icmp[addressAt..]) != ours) return true;

        var ihl = (inner[0] & 0x0F) * 4;
        if (ihl < 20 || ihl > inner.Length) return false;

        // The quote is at least the header and eight bytes, and usually no more (RFC 792). Its own UDP
        // checksum covers bytes that were not quoted, so it can only be adjusted, never checked - and only
        // when the quoted packet was a first fragment and the field is inside the quote.
        var l4ChecksumAt = -1;
        var isUdp = false;
        var firstFragment = (BinaryPrimitives.ReadUInt16BigEndian(inner[6..]) & 0x1FFF) == 0;
        if (firstFragment)
        {
            var l4 = 8 + ihl;
            if (inner[9] == ProtoUdp && l4 + 8 <= icmp.Length)
            {
                l4ChecksumAt = l4 + 6;
                isUdp = true;
            }
            else if (inner[9] == ProtoTcp && l4 + 18 <= icmp.Length)
            {
                l4ChecksumAt = l4 + 16;
            }
        }

        quoted = new QuotedPacket
        {
            Present = true,
            IpAt = 8,
            AddressAt = addressAt,
            L4ChecksumAt = l4ChecksumAt,
            L4IsUdp = isUdp,
        };
        return true;
    }

    /// <summary>
    /// The quoted packet's address and checksums, and the ICMP checksum for every one of those words. Every
    /// field sits at an even offset from the start of the ICMP message - the IP header length is a multiple
    /// of four - so adjusting the ICMP checksum word by word is exact.
    /// </summary>
    private static void RewriteQuoted(Span<byte> icmp, QuotedPacket quoted, uint from, uint to)
    {
        var icmpSum = ReadChecksum(icmp[2..]);

        BinaryPrimitives.WriteUInt32BigEndian(icmp[quoted.AddressAt..], to);
        icmpSum = Adjust32(icmpSum, from, to);

        var ipSumAt = quoted.IpAt + 10;
        var oldIpSum = ReadChecksum(icmp[ipSumAt..]);
        var newIpSum = Adjust32(oldIpSum, from, to);
        WriteChecksum(icmp[ipSumAt..], newIpSum);
        icmpSum = Adjust16(icmpSum, oldIpSum, newIpSum);

        if (quoted.L4ChecksumAt >= 0)
        {
            var old = ReadChecksum(icmp[quoted.L4ChecksumAt..]);
            if (!(quoted.L4IsUdp && old == 0))
            {
                var fresh = Adjust32(old, from, to);
                if (quoted.L4IsUdp) fresh = NonZero(fresh);
                WriteChecksum(icmp[quoted.L4ChecksumAt..], fresh);
                icmpSum = Adjust16(icmpSum, old, fresh);
            }
        }

        WriteChecksum(icmp[2..], icmpSum);
    }

    // ------------------------------------------------------------ RFC 1624

    /// <summary>HC' = ~(~HC + ~m + m') for a 32-bit field, taken as its two 16-bit words.</summary>
    internal static ushort Adjust32(ushort checksum, uint oldValue, uint newValue)
    {
        uint sum = (ushort)~checksum;
        sum += (ushort)~(oldValue >> 16);
        sum += (ushort)~(oldValue & 0xFFFF);
        sum += newValue >> 16;
        sum += newValue & 0xFFFF;
        return (ushort)~Fold(sum);
    }

    /// <summary>The same, for one 16-bit field.</summary>
    internal static ushort Adjust16(ushort checksum, ushort oldValue, ushort newValue)
    {
        uint sum = (ushort)~checksum;
        sum += (ushort)~oldValue;
        sum += newValue;
        return (ushort)~Fold(sum);
    }

    private static uint Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return sum;
    }

    /// <summary>A UDP checksum that comes out as zero is sent as 0xFFFF: zero on the wire means "no checksum".</summary>
    private static ushort NonZero(ushort checksum) => checksum == 0 ? (ushort)0xFFFF : checksum;

    private static ushort ReadChecksum(ReadOnlySpan<byte> at) => BinaryPrimitives.ReadUInt16BigEndian(at);

    private static void WriteChecksum(Span<byte> at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(at, value);
}
