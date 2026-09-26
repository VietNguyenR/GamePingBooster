using System.Buffers.Binary;
using GamePingBooster.Core.Paths;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Phase D: several tunnels behind one adapter, through the real <see cref="AdapterPump"/>,
/// <see cref="PathDispatcher"/> and <see cref="TunnelClient"/>, against FakeRelays that hand out different inner
/// addresses and drop anything with another source - relayd's anti-spoofing. docs/MULTI-TUNNEL.md, section 10.2.
/// </summary>
internal static partial class Program
{
    private static readonly uint SgServer = Addr(43, 132, 208, 47);
    private static readonly uint KrServer = Addr(162, 128, 82, 106);

    /// <summary>Two regions: "sg" (43.132.0.0/16) and "kr" (162.128.0.0/16).</summary>
    private static RegionTable TwoRegions() =>
        RegionTable.Build([("sg", ["43.132.0.0/16"]), ("kr", ["162.128.0.0/16"])]);

    /// <summary>A home tunnel on one relay and a second on another, both pumping, behind one dispatcher.</summary>
    private sealed class MultiRig : IDisposable
    {
        public FakeRelay HomeRelay { get; } = new(Psk, firstInner: 2);
        public FakeRelay OtherRelay { get; } = new(Psk, firstInner: 100);
        public Rig Rig { get; } = new();
        public TunnelClient Home { get; private set; } = null!;
        public TunnelClient Other { get; private set; } = null!;
        public PathDispatcher Dispatcher { get; private set; } = null!;
        public uint AdapterIp { get; private set; }

        public async Task StartAsync(ulong clientId)
        {
            Home = await Rig.StartAsync(HomeRelay, clientId);
            AdapterIp = Home.InnerIp;
            Dispatcher = new PathDispatcher(AdapterIp, TwoRegions(), Home);
            Home.Dispatcher = Dispatcher;

            Other = await Rig.OpenAsync(OtherRelay, clientId);
            Other.Dispatcher = Dispatcher;
            Other.StartPumping(Rig.Device, Rig.Cts.Token);
            Rig.Pump.SetDispatcher(Dispatcher);
        }

        /// <summary>sg stays home, kr leaves by the other tunnel.</summary>
        public void KrViaOther() => Dispatcher.SetPlan([null, Other]);

        public void Dispose()
        {
            Rig.Dispose();
            HomeRelay.Dispose();
            OtherRelay.Dispose();
        }
    }

    private static async Task TwoRegionsLeaveByTwoRelays()
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 40);
        m.KrViaOther();
        Check("The two relays handed out different inner addresses - the case that needs the rewrite",
            m.Home.InnerIp != m.Other.InnerIp, $"{m.Home.InnerIp:X8} on both");

        for (var i = 0; i < 200; i++) m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, i % 2 == 0 ? SgServer : KrServer, i));
        await WaitUntil(() => m.HomeRelay.Arrivals.Count + m.OtherRelay.Arrivals.Count >= 200);
        await Task.Delay(100);

        var home = m.HomeRelay.Arrivals.Select(a => a.Inner).ToList();
        var other = m.OtherRelay.Arrivals.Select(a => a.Inner).ToList();
        Check("Singapore's 100 packets leave by home, unchanged, in order",
            home.Count == 100 && home.All(p => Dst(p) == SgServer && Src(p) == m.AdapterIp) &&
            home.Select(SequenceOf).SequenceEqual(Enumerable.Range(0, 100).Select(i => i * 2)),
            $"{home.Count} at home");
        Check("Korea's 100 leave by the other relay with its inner address as the source, in order",
            other.Count == 100 && other.All(p => Dst(p) == KrServer && Src(p) == m.Other.InnerIp) &&
            other.Select(SequenceOf).SequenceEqual(Enumerable.Range(0, 100).Select(i => i * 2 + 1)),
            $"{other.Count} at the other relay");
        Check("Every rewritten packet's IP and UDP checksums verify from scratch",
            other.All(p => IpChecksumOk(p) && UdpChecksumOk(p)));
        Check("Neither relay dropped anything as spoofed", m.HomeRelay.Spoofed == 0 && m.OtherRelay.Spoofed == 0,
            $"home {m.HomeRelay.Spoofed}, other {m.OtherRelay.Spoofed}");

        var session = m.OtherRelay.SessionOf(m.Other.SessionId)!;
        var before = m.Rig.Device.ToWindows.Count;
        m.OtherRelay.SendToClient(session, GameChecked(KrServer, m.Other.InnerIp, 7));
        await WaitUntil(() => m.Rig.Device.ToWindows.Count > before);
        var back = m.Rig.Device.ToWindows.Last().Packet;
        Check("A reply from Korea reaches Windows addressed to the adapter, checksums intact",
            Dst(back) == m.AdapterIp && Src(back) == KrServer && IpChecksumOk(back) && UdpChecksumOk(back),
            $"to {Dst(back):X8}");
    }

    /// <summary>G1: the plan swapped a thousand times while three flows run - no flow may change relay.</summary>
    private static async Task RemappingNeverMovesAFlowInUse()
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 41);
        uint[] flows = [Addr(162, 128, 1, 1), Addr(162, 128, 2, 2), Addr(162, 128, 3, 3)];

        // They start while Korea is on home, so home is where they are stuck.
        foreach (var flow in flows) m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, flow, -1));
        await WaitUntil(() => m.HomeRelay.Arrivals.Count >= 3);

        for (var i = 0; i < 1_000; i++)
        {
            m.Dispatcher.SetPlan(i % 2 == 0 ? [null, m.Other] : [null, null]);
            foreach (var flow in flows) m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, flow, i));
        }
        await WaitUntil(() => m.HomeRelay.Arrivals.Count >= 3_003);
        await Task.Delay(100);

        Check("3,000 packets of three flows under 1,000 remaps all stay on the relay they started on",
            m.HomeRelay.Arrivals.Count == 3_003 && m.OtherRelay.Arrivals.IsEmpty,
            $"home {m.HomeRelay.Arrivals.Count}, other {m.OtherRelay.Arrivals.Count}");

        m.KrViaOther();
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, Addr(162, 128, 9, 9), 0));
        await WaitUntil(() => !m.OtherRelay.Arrivals.IsEmpty);
        Check("A server nobody is talking to yet follows the plan", m.OtherRelay.Arrivals.Count == 1);
    }

    /// <summary>G6: a tunnel that dies takes only its own destinations home; the rest carry on.</summary>
    private static async Task ARelayThatDiesSendsOnlyItsOwnHome()
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 42);
        m.KrViaOther();
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, 0));
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, SgServer, 0));
        await WaitUntil(() => m.OtherRelay.Arrivals.Count == 1 && m.HomeRelay.Arrivals.Count == 1);

        var released = m.Dispatcher.Remove(m.Other);
        m.Other.Dispose();
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, 1));
        await WaitUntil(() => m.HomeRelay.Arrivals.Count == 2);
        var moved = m.HomeRelay.Arrivals.Last().Inner;

        Check("Its stuck destination is released", released == 1, $"{released}");
        Check("and the next packet to it leaves by home, from the adapter's address",
            Dst(moved) == KrServer && Src(moved) == m.AdapterIp && m.HomeRelay.Spoofed == 0);
        Check("Korea is home in the plan now", m.Dispatcher.Plan.All(t => t is null));
    }

    /// <summary>5.9: a fault in the dispatcher costs one packet and its benefit, never a match in progress.</summary>
    private static async Task ADispatcherFaultCollapsesToHome()
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 43);
        m.KrViaOther();
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, 0));
        await WaitUntil(() => m.OtherRelay.Arrivals.Count == 1);

        var fired = 0;
        m.Dispatcher.TestFault = () => { if (Interlocked.Increment(ref fired) == 1) throw new InvalidOperationException("injected"); };
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, 1));   // lost to the fault
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, 2));   // in use: keeps its tunnel
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, Addr(162, 128, 5, 5), 3));   // new: home
        await WaitUntil(() => m.OtherRelay.Arrivals.Count >= 2 && m.HomeRelay.Arrivals.Count >= 1);
        await Task.Delay(100);

        Check("The dispatcher says why it collapsed", m.Dispatcher.FaultReason?.Contains("injected") == true, m.Dispatcher.FaultReason ?? "no reason");
        Check("The faulting packet is the only one lost; the pump carries on",
            m.OtherRelay.Arrivals.Select(a => SequenceOf(a.Inner)).SequenceEqual([0, 2]) && m.HomeRelay.Arrivals.Count == 1,
            $"other {string.Join(",", m.OtherRelay.Arrivals.Select(a => SequenceOf(a.Inner)))}, home {m.HomeRelay.Arrivals.Count}");
        Check("A destination in use keeps its tunnel; a new one goes home", Dst(m.HomeRelay.Arrivals.Single().Inner) == Addr(162, 128, 5, 5));
        m.KrViaOther();
        Check("and no plan is accepted after the collapse", m.Dispatcher.Plan.All(t => t is null));
    }

    /// <summary>
    /// 5.2: an ICMP error the other relay's kernel sends about our packet quotes that relay's inner address. Windows
    /// must see the adapter's, outside and inside the quote, or it drops the error - path-MTU discovery and "port
    /// unreachable" arrive this way.
    /// </summary>
    private static async Task AnIcmpErrorReachesWindowsAboutItsOwnPacket()
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 44);
        m.KrViaOther();
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, 0));
        await WaitUntil(() => m.OtherRelay.Arrivals.Count == 1);
        var quoted = m.OtherRelay.Arrivals.Single().Inner;

        var error = IcmpError(KrServer, m.Other.InnerIp, quoted);
        var before = m.Rig.Device.ToWindows.Count;
        m.OtherRelay.SendToClient(m.OtherRelay.SessionOf(m.Other.SessionId)!, error);
        await WaitUntil(() => m.Rig.Device.ToWindows.Count > before);
        var got = m.Rig.Device.ToWindows.Last().Packet;
        var quote = got.AsSpan(28);

        Check("The error is addressed to the adapter", Dst(got) == m.AdapterIp);
        Check("and quotes the packet as Windows sent it - from the adapter's address",
            BinaryPrimitives.ReadUInt32BigEndian(quote[12..]) == m.AdapterIp);
        Check("Outer IP, quoted IP and ICMP checksums all verify",
            IpChecksumOk(got) && Checksum(quote[..20]) == 0 && Checksum(got.AsSpan(20)) == 0);
    }

    /// <summary>5.2 and 5.7: home reconnects to a relay that hands out another address; the adapter keeps its own.</summary>
    private static async Task AReplacedHomeRewritesInsteadOfReaddressing()
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 45);
        using var third = new FakeRelay(Psk, firstInner: 150);
        m.KrViaOther();
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, SgServer, 0));
        await WaitUntil(() => m.HomeRelay.Arrivals.Count == 1);

        // ReconnectAsync's order: the old home lost, a new one handshaken, then put in place.
        m.Rig.Pump.SetHome(null);
        m.Dispatcher.LoseHome();
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, 1));   // a match on the other tunnel carries on
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, SgServer, 1));   // for home, while there is none: dropped
        await WaitUntil(() => m.OtherRelay.Arrivals.Count == 1);
        await Task.Delay(100);
        Check("With no home, the other tunnel still carries its own; what is for home is dropped, not queued behind it",
            m.OtherRelay.Arrivals.Count == 1 && m.HomeRelay.Arrivals.Count == 1 && m.Rig.Pump.DroppedNoTarget == 1,
            $"other {m.OtherRelay.Arrivals.Count}, home {m.HomeRelay.Arrivals.Count}, dropped {m.Rig.Pump.DroppedNoTarget}");

        var fresh = await m.Rig.OpenAsync(third, 45);
        fresh.Dispatcher = m.Dispatcher;
        fresh.StartPumping(m.Rig.Device, m.Rig.Cts.Token);
        m.Dispatcher.ReplaceHome(fresh);
        m.Rig.Pump.SetHome(fresh);
        m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, SgServer, 2));
        await WaitUntil(() => !third.Arrivals.IsEmpty);
        var sent = third.Arrivals.FirstOrDefault().Inner;

        Check("The new home carries Singapore from its own inner address, checksums intact",
            sent is not null && Src(sent) == fresh.InnerIp && fresh.InnerIp != m.AdapterIp && IpChecksumOk(sent) && UdpChecksumOk(sent) &&
            third.Spoofed == 0, sent is null ? "nothing arrived" : $"from {Src(sent):X8}");
        var session = third.SessionOf(fresh.SessionId)!;
        var before = m.Rig.Device.ToWindows.Count;
        third.SendToClient(session, GameChecked(SgServer, fresh.InnerIp, 9));
        await WaitUntil(() => m.Rig.Device.ToWindows.Count > before);
        Check("and its replies reach the adapter's unchanged address", Dst(m.Rig.Device.ToWindows.Last().Packet) == m.AdapterIp);
    }

    // ------------------------------------------------------------ packets with every checksum

    private static uint Src(byte[] p) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12));
    private static uint Dst(byte[] p) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16));

    /// <summary><see cref="Game"/> with a real UDP checksum, so a rewrite that forgets the pseudo-header shows.</summary>
    private static byte[] GameChecked(uint src, uint dst, int sequence, int payload = 60)
    {
        var packet = Game(src, dst, sequence, payload);
        var sum = UdpSum(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26), sum == 0 ? (ushort)0xFFFF : sum);
        return packet;
    }

    private static bool IpChecksumOk(byte[] p) => Checksum(p.AsSpan(0, (p[0] & 0x0F) * 4)) == 0;

    /// <summary>Recomputed from scratch over the pseudo-header, independently of InnerNat. Zero means "none".</summary>
    private static bool UdpChecksumOk(byte[] p)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(26)) == 0) return true;
        var copy = (byte[])p.Clone();
        copy[26] = copy[27] = 0;
        var sum = UdpSum(copy);
        return BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(26)) == (sum == 0 ? 0xFFFF : sum);
    }

    private static ushort UdpSum(byte[] p)
    {
        var ihl = (p[0] & 0x0F) * 4;
        var udp = p.AsSpan(ihl);
        var pseudo = new byte[12 + udp.Length + (udp.Length & 1)];
        p.AsSpan(12, 8).CopyTo(pseudo);
        pseudo[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10), (ushort)udp.Length);
        udp.CopyTo(pseudo.AsSpan(12));
        return Checksum(pseudo);
    }

    /// <summary>Destination unreachable / port unreachable from <paramref name="router"/>, quoting 28 bytes of <paramref name="original"/>.</summary>
    private static byte[] IcmpError(uint router, uint to, byte[] original)
    {
        var packet = new byte[20 + 8 + 28];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), router);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), to);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
        packet[20] = 3;
        packet[21] = 3;
        original.AsSpan(0, 28).CopyTo(packet.AsSpan(28));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), Checksum(packet.AsSpan(20)));
        return packet;
    }
}
