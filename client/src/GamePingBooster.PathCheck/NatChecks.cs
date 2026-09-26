using System.Buffers.Binary;
using System.Diagnostics;
using GamePingBooster.Core.Net;
using static GamePingBooster.PathCheck.Packets;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    /// <summary>The adapter's address - the home tunnel's at connect.</summary>
    private static readonly uint Adapter = Addr(10, 77, 0, 2);

    /// <summary>A second relay's address for this client - what a secondary tunnel must carry.</summary>
    private static readonly uint Inner = Addr(10, 77, 0, 9);

    private const int RandomPackets = 50_000;

    private static void NatChecks()
    {
        UdpUplinkVerifiesAndUndoes();
        UdpDownlinkVerifiesAndUndoes();
        TcpBothWaysVerifyAndUndo();
        IcmpEchoBothWays();
        UdpWithoutChecksumStaysWithout();
        UdpChecksumNeverBecomesNone();
        FirstFragmentCarriesTheWholeDatagramsChecksum();
        LaterFragmentChangesOnlyTheIpHeader();
        IcmpErrorAboutOurUdpReachesWindowsAsItsOwn();
        IcmpErrorAboutOurTcpReachesWindowsAsItsOwn();
        IcmpErrorWindowsSendsGoesBackAsTheRelayDeliveredIt();
        IcmpErrorAboutSomebodyElseKeepsItsQuote();
        OtherAddressesAreNotOurs();
        MalformedPacketsAreRefusedUntouched();
        SameAddressIsNothingToDo();
        CostPerPacket();
    }

    // ------------------------------------------------------------ properties

    private static void UdpUplinkVerifiesAndUndoes()
    {
        var rng = new Random(Seed);
        var failure = RoundTrips(RandomPackets, () =>
        {
            var server = RandomServer(rng);
            var udp = UdpDatagram(Adapter, server, (ushort)rng.Next(1024, 65536), (ushort)rng.Next(1, 65536),
                RandomBytes(rng, rng.Next(0, 1400)), noChecksum: rng.Next(10) == 0);
            return Ip(Adapter, server, Udp, udp, rng, optionWords: rng.Next(3) == 0 ? rng.Next(1, 11) : 0);
        }, uplink: true, packet => Src(packet) == Inner && IpHeaderOk(packet) && UdpOk(packet));
        Check($"UDP, Windows to a second relay: {RandomPackets:N0} random packets verify and undo", failure is null, failure ?? "");
    }

    private static void UdpDownlinkVerifiesAndUndoes()
    {
        var rng = new Random(Seed + 1);
        var failure = RoundTrips(RandomPackets, () =>
        {
            var server = RandomServer(rng);
            var udp = UdpDatagram(server, Inner, (ushort)rng.Next(1, 65536), (ushort)rng.Next(1024, 65536),
                RandomBytes(rng, rng.Next(0, 1400)), noChecksum: rng.Next(10) == 0);
            return Ip(server, Inner, Udp, udp, rng, optionWords: rng.Next(3) == 0 ? rng.Next(1, 11) : 0);
        }, uplink: false, packet => Dst(packet) == Adapter && IpHeaderOk(packet) && UdpOk(packet));
        Check($"UDP, a second relay to Windows: {RandomPackets:N0} random packets verify and undo", failure is null, failure ?? "");
    }

    private static void TcpBothWaysVerifyAndUndo()
    {
        var rng = new Random(Seed + 2);
        var up = RoundTrips(RandomPackets, () =>
        {
            var server = RandomServer(rng);
            return Ip(Adapter, server, Tcp, TcpSegment(Adapter, server, rng, RandomBytes(rng, rng.Next(0, 1360))), rng,
                optionWords: rng.Next(4) == 0 ? rng.Next(1, 11) : 0);
        }, uplink: true, packet => Src(packet) == Inner && IpHeaderOk(packet) && TcpOk(packet));
        var down = RoundTrips(RandomPackets, () =>
        {
            var server = RandomServer(rng);
            return Ip(server, Inner, Tcp, TcpSegment(server, Inner, rng, RandomBytes(rng, rng.Next(0, 1360))), rng);
        }, uplink: false, packet => Dst(packet) == Adapter && IpHeaderOk(packet) && TcpOk(packet));
        Check($"TCP, both ways: {2 * RandomPackets:N0} random packets verify and undo", up is null && down is null, up ?? down ?? "");
    }

    private static void IcmpEchoBothWays()
    {
        var rng = new Random(Seed + 3);
        var up = RoundTrips(10_000, () =>
        {
            var server = RandomServer(rng);
            return Ip(Adapter, server, Icmp, IcmpEcho(8, (ushort)rng.Next(65536), (ushort)rng.Next(65536), RandomBytes(rng, rng.Next(0, 64))), rng);
        }, uplink: true, packet => Src(packet) == Inner && IpHeaderOk(packet) && IcmpOk(packet));
        var down = RoundTrips(10_000, () =>
        {
            var server = RandomServer(rng);
            return Ip(server, Inner, Icmp, IcmpEcho(0, (ushort)rng.Next(65536), (ushort)rng.Next(65536), RandomBytes(rng, rng.Next(0, 64))), rng);
        }, uplink: false, packet => Dst(packet) == Adapter && IpHeaderOk(packet) && IcmpOk(packet));
        Check("ICMP echo, both ways: verifies and undoes (ICMP has no pseudo-header to adjust)", up is null && down is null, up ?? down ?? "");
    }

    /// <summary>
    /// Rewrites each packet one way, checks it by full recomputation, rewrites it back and compares with the
    /// original byte for byte. Returns the first failure, described, or null.
    /// </summary>
    private static string? RoundTrips(int count, Func<byte[]> make, bool uplink, Func<byte[], bool> valid)
    {
        for (var i = 0; i < count; i++)
        {
            var original = make();
            var packet = (byte[])original.Clone();
            var (from, to) = uplink ? (Adapter, Inner) : (Inner, Adapter);

            var there = uplink ? InnerNat.RewriteSource(packet, from, to) : InnerNat.RewriteDestination(packet, from, to);
            if (there != InnerNat.Outcome.Rewritten) return $"seed {Seed}, packet {i}: {there}, expected Rewritten ({Convert.ToHexString(original)})";
            if (!valid(packet)) return $"seed {Seed}, packet {i}: a checksum does not verify after the rewrite ({Convert.ToHexString(original)})";

            var back = uplink ? InnerNat.RewriteSource(packet, to, from) : InnerNat.RewriteDestination(packet, to, from);
            if (back != InnerNat.Outcome.Rewritten) return $"seed {Seed}, packet {i}: undoing it gave {back}";
            if (!packet.AsSpan().SequenceEqual(original)) return $"seed {Seed}, packet {i}: undone, but not byte for byte ({Convert.ToHexString(original)} became {Convert.ToHexString(packet)})";
        }
        return null;
    }

    // ------------------------------------------------------------ named cases

    private static void UdpWithoutChecksumStaysWithout()
    {
        var rng = new Random(Seed + 4);
        var server = RandomServer(rng);
        var packet = Ip(Adapter, server, Udp, UdpDatagram(Adapter, server, 50000, 7331, RandomBytes(rng, 100), noChecksum: true), rng);
        InnerNat.RewriteSource(packet, Adapter, Inner);
        var field = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(26));
        Check("UDP sent without a checksum (0) is still without one", field == 0 && IpHeaderOk(packet), $"checksum field became {field:X4}");
    }

    /// <summary>
    /// The one value the incremental arithmetic can produce that UDP may not carry: a checksum that comes out
    /// as zero must be written 0xFFFF, or the receiver reads "no checksum" and the datagram is unprotected.
    /// Searched for rather than hand-built, so the case really comes out of the arithmetic.
    /// </summary>
    private static void UdpChecksumNeverBecomesNone()
    {
        var rng = new Random(Seed + 5);
        var found = 0;
        string? failure = null;
        for (var i = 0; i < 2_000_000 && found < 3 && failure is null; i++)
        {
            var server = RandomServer(rng);
            var payload = RandomBytes(rng, 16);
            var udp = UdpDatagram(Adapter, server, (ushort)rng.Next(1024, 65536), 7331, payload);

            // Would the checksum for the rewritten datagram, computed in full, come out as zero?
            var copy = (byte[])udp.Clone();
            copy[6] = copy[7] = 0;
            if (Finish(Sum(copy, Pseudo(Inner, server, Udp, copy.Length))) != 0) continue;

            found++;
            var packet = Ip(Adapter, server, Udp, udp, rng);
            InnerNat.RewriteSource(packet, Adapter, Inner);
            var field = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(26));
            if (field != 0xFFFF || !UdpOk(packet)) failure = $"seed {Seed}, try {i}: the field is {field:X4}, expected FFFF";
        }
        Check($"UDP checksum that comes out zero is written FFFF, never 'none' ({found} found)",
            failure is null && found > 0, failure ?? "no such packet found - widen the search");
    }

    /// <summary>
    /// A first fragment's UDP checksum covers the whole datagram, most of which is in later fragments this side
    /// never sees together. The rewritten field must equal the checksum of the WHOLE datagram under the new
    /// address - which only an incremental adjustment can produce.
    /// </summary>
    private static void FirstFragmentCarriesTheWholeDatagramsChecksum()
    {
        var rng = new Random(Seed + 6);
        string? failure = null;
        for (var i = 0; i < 5_000 && failure is null; i++)
        {
            var server = RandomServer(rng);
            var whole = UdpDatagram(Adapter, server, (ushort)rng.Next(1024, 65536), 7331, RandomBytes(rng, rng.Next(1500, 4000)));
            var firstBytes = 8 + 8 * rng.Next(1, 170);
            var packet = Ip(Adapter, server, Udp, whole.AsSpan(0, firstBytes), rng, moreFragments: true);

            InnerNat.RewriteSource(packet, Adapter, Inner);
            var field = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(26));
            var expected = UdpChecksum(Inner, server, whole);
            if (field != expected || !IpHeaderOk(packet)) failure = $"seed {Seed}, fragment {i}: {field:X4}, expected {expected:X4}";
        }
        Check("First fragment: its UDP checksum is the whole datagram's under the new address", failure is null, failure ?? "");
    }

    private static void LaterFragmentChangesOnlyTheIpHeader()
    {
        var rng = new Random(Seed + 7);
        var server = RandomServer(rng);
        var payload = RandomBytes(rng, 800);
        var packet = Ip(Adapter, server, Udp, payload, rng, fragmentOffset8: 185, moreFragments: rng.Next(2) == 0);
        InnerNat.RewriteSource(packet, Adapter, Inner);
        Check("A later fragment: address and header checksum change, the payload does not",
            Src(packet) == Inner && IpHeaderOk(packet) && packet.AsSpan(20).SequenceEqual(payload),
            "a byte past the IP header changed, or the header does not verify");
    }

    /// <summary>
    /// The relay's kernel answers about a packet we sent through it - port unreachable, time exceeded, fragmentation
    /// needed - quoting that packet as it left the relay's NAT: from OUR inner address on that relay. Windows must
    /// receive a quote of the packet IT sent, from the adapter's address, or it drops the error: path-MTU
    /// discovery for the lobby and "port unreachable" on a dead socket both depend on this.
    /// </summary>
    private static void IcmpErrorAboutOurUdpReachesWindowsAsItsOwn()
    {
        var rng = new Random(Seed + 8);
        string? failure = null;
        for (var i = 0; i < 20_000 && failure is null; i++)
        {
            var server = RandomServer(rng);
            var sent = Ip(Adapter, server, Udp,
                UdpDatagram(Adapter, server, (ushort)rng.Next(1024, 65536), (ushort)rng.Next(1, 65536), RandomBytes(rng, rng.Next(0, 600))), rng,
                optionWords: rng.Next(4) == 0 ? rng.Next(1, 4) : 0);
            var onTheWire = (byte[])sent.Clone();
            InnerNat.RewriteSource(onTheWire, Adapter, Inner);

            // The kernel quotes the header and 8 bytes (RFC 792), or more (RFC 1812 allows up to 576 in all).
            var quote = rng.Next(3) switch { 0 => Ihl(onTheWire) + 8, 1 => onTheWire.Length, _ => Math.Min(onTheWire.Length, 548) };
            var (type, code) = rng.Next(3) switch { 0 => ((byte)3, (byte)3), 1 => ((byte)11, (byte)0), _ => ((byte)3, (byte)4) };
            var error = Ip(RandomServer(rng), Inner, Icmp, IcmpError(type, code, onTheWire, quote), rng);

            var outcome = InnerNat.RewriteDestination(error, Inner, Adapter);
            var icmp = error.AsSpan(Ihl(error));
            var quoted = icmp[8..];
            if (outcome != InnerNat.Outcome.Rewritten) failure = $"packet {i}: {outcome}";
            else if (Dst(error) != Adapter || !IpHeaderOk(error)) failure = $"packet {i}: outer header wrong or does not verify";
            else if (!IcmpOk(error)) failure = $"packet {i}: the ICMP checksum does not verify";
            else if (!quoted.SequenceEqual(sent.AsSpan(0, quoted.Length))) failure = $"packet {i}: the quote is not what Windows sent ({Convert.ToHexString(quoted)} vs {Convert.ToHexString(sent.AsSpan(0, quoted.Length))})";
        }
        Check("ICMP error quoting our UDP: Windows gets its own packet back, every checksum verifies", failure is null, failure ?? "");
    }

    private static void IcmpErrorAboutOurTcpReachesWindowsAsItsOwn()
    {
        var rng = new Random(Seed + 9);
        string? failure = null;
        for (var i = 0; i < 20_000 && failure is null; i++)
        {
            var server = RandomServer(rng);
            var sent = Ip(Adapter, server, Tcp, TcpSegment(Adapter, server, rng, RandomBytes(rng, rng.Next(0, 600))), rng);
            var onTheWire = (byte[])sent.Clone();
            InnerNat.RewriteSource(onTheWire, Adapter, Inner);

            // Eight bytes of TCP do not reach its checksum; a longer quote does.
            var quote = rng.Next(2) == 0 ? Ihl(onTheWire) + 8 : Math.Min(onTheWire.Length, 548);
            var error = Ip(RandomServer(rng), Inner, Icmp, IcmpError(3, 4, onTheWire, quote), rng);

            InnerNat.RewriteDestination(error, Inner, Adapter);
            var quoted = error.AsSpan(Ihl(error) + 8);
            if (Dst(error) != Adapter || !IpHeaderOk(error) || !IcmpOk(error)) failure = $"packet {i}: outer or ICMP checksum wrong";
            else if (!quoted.SequenceEqual(sent.AsSpan(0, quoted.Length))) failure = $"packet {i}: the quote is not what Windows sent";
        }
        Check("ICMP error quoting our TCP (8 bytes and longer): Windows gets its own segment back", failure is null, failure ?? "");
    }

    /// <summary>
    /// The other direction: Windows answers a packet it received with an ICMP error - port unreachable after the
    /// game closed a socket - quoting that packet as it saw it, TO the adapter's address. Leaving by the second
    /// relay, the quote must be the packet as that relay delivered it, to its inner address, or the relay's
    /// conntrack cannot match it to the flow and drops it.
    /// </summary>
    private static void IcmpErrorWindowsSendsGoesBackAsTheRelayDeliveredIt()
    {
        var rng = new Random(Seed + 10);
        string? failure = null;
        for (var i = 0; i < 20_000 && failure is null; i++)
        {
            var server = RandomServer(rng);
            var delivered = Ip(server, Inner, Udp,
                UdpDatagram(server, Inner, (ushort)rng.Next(1, 65536), (ushort)rng.Next(1024, 65536), RandomBytes(rng, rng.Next(0, 300))), rng);
            var received = (byte[])delivered.Clone();
            InnerNat.RewriteDestination(received, Inner, Adapter);

            var error = Ip(Adapter, server, Icmp, IcmpError(3, 3, received, rng.Next(2) == 0 ? Ihl(received) + 8 : received.Length), rng);
            var outcome = InnerNat.RewriteSource(error, Adapter, Inner);
            var quoted = error.AsSpan(Ihl(error) + 8);
            if (outcome != InnerNat.Outcome.Rewritten || Src(error) != Inner || !IpHeaderOk(error) || !IcmpOk(error)) failure = $"packet {i}: {outcome}, or a checksum does not verify";
            else if (!quoted.SequenceEqual(delivered.AsSpan(0, quoted.Length))) failure = $"packet {i}: the quote is not what the relay delivered";
        }
        Check("ICMP error Windows sends: the quote goes back as the relay delivered it", failure is null, failure ?? "");
    }

    private static void IcmpErrorAboutSomebodyElseKeepsItsQuote()
    {
        var rng = new Random(Seed + 11);
        var stranger = Addr(10, 77, 0, 40);
        var server = RandomServer(rng);
        var theirs = Ip(stranger, server, Udp, UdpDatagram(stranger, server, 40000, 7331, RandomBytes(rng, 40)), rng);
        var error = Ip(RandomServer(rng), Inner, Icmp, IcmpError(3, 3, theirs, theirs.Length), rng);
        var quoteBefore = error.AsSpan(28).ToArray();

        InnerNat.RewriteDestination(error, Inner, Adapter);
        Check("ICMP error quoting another address: only the outer header changes",
            Dst(error) == Adapter && IpHeaderOk(error) && IcmpOk(error) && error.AsSpan(28).SequenceEqual(quoteBefore),
            "the quote was touched, or a checksum does not verify");
    }

    private static void OtherAddressesAreNotOurs()
    {
        var rng = new Random(Seed + 12);
        var server = RandomServer(rng);
        var up = Ip(Addr(10, 77, 0, 3), server, Udp, UdpDatagram(Addr(10, 77, 0, 3), server, 1, 2, RandomBytes(rng, 10)), rng);
        var down = Ip(server, Addr(10, 77, 0, 3), Udp, UdpDatagram(server, Addr(10, 77, 0, 3), 1, 2, RandomBytes(rng, 10)), rng);
        var upBefore = (byte[])up.Clone();
        var downBefore = (byte[])down.Clone();
        Check("A source or destination that is not ours: NotOurs, nothing changed",
            InnerNat.RewriteSource(up, Adapter, Inner) == InnerNat.Outcome.NotOurs && up.SequenceEqual(upBefore) &&
            InnerNat.RewriteDestination(down, Inner, Adapter) == InnerNat.Outcome.NotOurs && down.SequenceEqual(downBefore),
            "it was rewritten anyway");
    }

    private static void MalformedPacketsAreRefusedUntouched()
    {
        var rng = new Random(Seed + 13);
        var server = RandomServer(rng);
        var good = Ip(Adapter, server, Udp, UdpDatagram(Adapter, server, 1, 2, RandomBytes(rng, 20)), rng);

        byte[] With(Action<byte[]> change)
        {
            var copy = (byte[])good.Clone();
            change(copy);
            return copy;
        }

        byte[] Truncated(byte protocol, int l4Bytes)
        {
            var packet = Ip(Adapter, server, protocol, RandomBytes(rng, l4Bytes), rng);
            return packet;
        }

        var cases = new (string Name, byte[] Packet)[]
        {
            ("shorter than a header", good[..19]),
            ("IPv6", With(p => p[0] = 0x60)),
            ("header length under 20", With(p => p[0] = 0x44)),
            ("total length past the buffer", With(p => BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)(p.Length + 1)))),
            ("total length under the header", With(p => BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 19))),
            ("UDP header cut short", Truncated(Udp, 4)),
            ("TCP header cut short", Truncated(Tcp, 12)),
            ("ICMP header cut short", Truncated(Icmp, 4)),
            // Windows quoting a packet it received - to the adapter's address, so the quote is ours - whose
            // header claims 60 bytes where only 24 were quoted.
            ("ICMP error quoting a header longer than the quote",
                Ip(Adapter, server, Icmp, IcmpError(3, 3, Received(server, rng, p => p[0] = 0x4F)[..24], 24), rng)),
        };

        var bad = new List<string>();
        foreach (var (name, packet) in cases)
        {
            var before = (byte[])packet.Clone();
            try
            {
                var outcome = InnerNat.RewriteSource(packet, Adapter, Inner);
                if (outcome != InnerNat.Outcome.Malformed || !packet.AsSpan().SequenceEqual(before)) bad.Add($"{name}: {outcome}");
            }
            catch (Exception ex)
            {
                // On the packet path an exception ends the uplink thread - worse than any wrong answer.
                bad.Add($"{name}: threw {ex.GetType().Name}");
            }
        }
        Check($"Malformed packets are refused whole, never half rewritten ({cases.Length} shapes)", bad.Count == 0, string.Join("; ", bad));
    }

    private static byte[] Received(uint server, Random rng, Action<byte[]> change)
    {
        var packet = Ip(server, Adapter, Udp, UdpDatagram(server, Adapter, 1, 2, RandomBytes(rng, 20)), rng);
        change(packet);
        return packet;
    }

    private static void SameAddressIsNothingToDo()
    {
        var rng = new Random(Seed + 14);
        var server = RandomServer(rng);
        var packet = Ip(Adapter, server, Udp, UdpDatagram(Adapter, server, 1, 2, RandomBytes(rng, 20)), rng);
        var before = (byte[])packet.Clone();
        Check("A relay that handed out the adapter's own address: nothing to rewrite",
            InnerNat.RewriteSource(packet, Adapter, Adapter) == InnerNat.Outcome.Rewritten && packet.SequenceEqual(before),
            "the packet changed");
    }

    /// <summary>Reported, and failed only when absurd: this is a developer machine, not a benchmark rig.</summary>
    private static void CostPerPacket()
    {
        var rng = new Random(Seed + 15);
        var server = RandomServer(rng);
        var packet = Ip(Adapter, server, Udp, UdpDatagram(Adapter, server, 50000, 7331, RandomBytes(rng, 120)), rng);
        const int rounds = 2_000_000;
        for (var i = 0; i < 10_000; i++) InnerNat.RewriteSource(packet, i % 2 == 0 ? Adapter : Inner, i % 2 == 0 ? Inner : Adapter);

        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < rounds; i++)
        {
            if (i % 2 == 0) InnerNat.RewriteSource(packet, Adapter, Inner);
            else InnerNat.RewriteSource(packet, Inner, Adapter);
        }
        var ns = Stopwatch.GetElapsedTime(started).TotalNanoseconds / rounds;
        Check($"Cost: {ns:F0} ns a packet (a game sends 50-150 a second)", ns < 2_000, $"{ns:F0} ns is far past anything the packet path can afford");
    }
}
