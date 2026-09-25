using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using GamePingBooster.Core.Protocol;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The packet path, driven in-process: a fake adapter, the real <see cref="AdapterPump"/> and
/// <see cref="TunnelClient"/>, and a fake relay on loopback that keeps relayd's rules. No driver, no VPS, no
/// Administrator. See docs/MULTI-TUNNEL.md, section 10.2.
///
///     dotnet run --project client/src/GamePingBooster.TunnelCheck
///
/// Phase A moved the adapter's read out of TunnelClient into AdapterPump and promised nothing a player could
/// see would change. Each scenario here is one thing that must still be true - byte for byte on the wire,
/// packet for packet in the counters - or one thing that changed on purpose, checked as such.
/// </summary>
internal static partial class Program
{
    private static int _failures;
    private static readonly byte[] Psk = "tunnelcheck-pre-shared-key-0123456789"u8.ToArray();
    private static readonly ConcurrentQueue<string> Log = new();

    private static int Main()
    {
        var scenarios = new (string Title, Func<Task> Run)[]
        {
            ("Uplink", UplinkCarriesExactlyWhatTheOldLoopCarried),
            ("Downlink", DownlinkDeliversEveryPacketAndKeepsProbesOut),
            ("Keepalive", KeepaliveFindsTheRelay),
            ("Swapping the tunnel under the pump", ASwapLeavesPacketsWaitingInTheAdapter),
            ("Entry switching", AMoveToAnotherDoorUnderLoadLosesNothing),
            ("Reconnect", AReconnectResumesTheSession),
            ("Teardown", TeardownNeverTouchesAnEndedSession),
            ("Faults", Faults),
            ("Latency", LatencyAddedByThePump),
            ("Region plan record", ARegionPlanRecordKeepsItsShape),
            ("Two regions, two relays", TwoRegionsLeaveByTwoRelays),
            ("Remapping under load", RemappingNeverMovesAFlowInUse),
            ("One relay dies", ARelayThatDiesSendsOnlyItsOwnHome),
            ("Dispatcher fault", ADispatcherFaultCollapsesToHome),
            ("ICMP error through another relay", AnIcmpErrorReachesWindowsAboutItsOwnPacket),
            ("Home replaced", AReplacedHomeRewritesInsteadOfReaddressing),
            ("The recorder follows the match", TheRecorderFollowsTheTunnelCarryingTheMatch),
        };

        foreach (var (title, run) in scenarios)
        {
            Console.WriteLine();
            Console.WriteLine($"{title}:");
            try
            {
                run().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"  FAIL  the scenario threw: {ex}");
            }
        }

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All tunnel checks passed.");
            return 0;
        }
        Console.WriteLine($"{_failures} tunnel check(s) FAILED. The client's log from the run:");
        foreach (var line in Log.TakeLast(40)) Console.WriteLine($"    {line}");
        return 1;
    }

    internal static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            Console.WriteLine($"  ok    {name}");
            return;
        }
        _failures++;
        Console.WriteLine($"  FAIL  {name}: {detail}");
    }

    // ------------------------------------------------------------ a rig: one device, a pump, tunnels

    private sealed class Rig : IDisposable
    {
        public FakeDevice Device { get; } = new();
        public AdapterPump Pump { get; }
        public CancellationTokenSource Cts { get; } = new();
        private readonly List<TunnelClient> _tunnels = [];

        public Rig()
        {
            Pump = new AdapterPump(Device, s => Log.Enqueue(s));
            Pump.Start();
        }

        /// <summary>Handshakes, starts the downlink and keepalive, and points the pump at it - StartTunnel's steps.</summary>
        public async Task<TunnelClient> StartAsync(FakeRelay relay, ulong clientId, int door = 0)
        {
            var tunnel = await OpenAsync(relay, clientId, door);
            tunnel.StartPumping(Device, Cts.Token);
            Pump.SetHome(tunnel);
            return tunnel;
        }

        public async Task<TunnelClient> OpenAsync(FakeRelay relay, ulong clientId, int door = 0)
        {
            var tunnel = new TunnelClient(relay.Door(door), TunnelAuth.FromPsk(Psk), clientId, s => Log.Enqueue(s));
            await tunnel.HandshakeAsync(attempts: 3, Cts.Token);
            _tunnels.Add(tunnel);
            return tunnel;
        }

        public void Dispose()
        {
            // The engine's order: the reader, then the tunnels, then the session.
            Pump.Dispose();
            foreach (var tunnel in _tunnels)
            {
                // A scenario may have put one away already, as the engine does on a swap.
                try { tunnel.Dispose(); } catch (ObjectDisposedException) { }
            }
            Device.EndSession();
            Cts.Cancel();
            Cts.Dispose();
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }
        return condition();
    }

    // ------------------------------------------------------------ packets

    private static readonly uint[] Servers = [Addr(43, 132, 208, 47), Addr(162, 128, 82, 106), Addr(34, 146, 147, 138)];

    private static uint Addr(int a, int b, int c, int d) => (uint)(a << 24 | b << 16 | c << 8 | d);

    private static uint U32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    /// <summary>A game datagram carrying its sequence number, checksums correct.</summary>
    private static byte[] Game(uint src, uint dst, int sequence, int payload = 60)
    {
        var packet = new byte[20 + 8 + payload];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = 128;
        packet[9] = 17;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), src);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), dst);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 17913);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(8 + payload));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(28), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
        return packet;
    }

    private static int SequenceOf(byte[] packet) => BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(28));

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>
    /// What the uplink loop before Phase A sent for one packet, written again from its rules rather than
    /// called: null for what it dropped as local noise, else the Data message. An independent model, so a
    /// change to the shared code cannot move both sides at once.
    /// </summary>
    private static byte[]? LegacyWire(byte[] packet, ulong sessionId, uint inner)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4) return null;
        var dst = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16));
        if ((dst & 0xF0000000) == 0xE0000000) return null;           // multicast
        if (dst == 0xFFFFFFFF) return null;                          // limited broadcast
        if ((dst & 0xFFFF0000) == 0xA9FE0000) return null;           // 169.254/16
        if (dst == (inner | 0xFF)) return null;                      // the tunnel's /24 broadcast
        var wire = new byte[9 + packet.Length];
        wire[0] = 0x33;
        BinaryPrimitives.WriteUInt64BigEndian(wire.AsSpan(1), sessionId);
        packet.CopyTo(wire.AsSpan(9));
        return wire;
    }

    /// <summary>Everything Windows pushes into an adapter besides the game: the local noise the tunnel drops.</summary>
    private static IEnumerable<byte[]> Noise(uint inner)
    {
        yield return Game(inner, Addr(224, 0, 0, 251), -1);       // mDNS
        yield return Game(inner, Addr(239, 255, 255, 250), -1);   // SSDP
        yield return Game(inner, 0xFFFFFFFF, -1);                 // limited broadcast
        yield return Game(inner, inner | 0xFF, -1);               // NetBIOS at the /24 broadcast
        yield return Game(inner, Addr(169, 254, 3, 4), -1);       // link-local
        var v6 = new byte[60];
        v6[0] = 0x60;
        yield return v6;                                          // IPv6
        yield return new byte[19];                                // a runt
    }
}
