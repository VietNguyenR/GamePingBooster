using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    /// <summary>
    /// The heart of Phase A. A mixture of game traffic and every kind of local noise goes through the pump; the
    /// relay must receive exactly the datagrams the old per-tunnel loop sent - same order, same bytes - and the
    /// counters must say what they said.
    /// </summary>
    private static async Task UplinkCarriesExactlyWhatTheOldLoopCarried()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 1);
        var inner = U32(tunnel.Session.ClientIp);

        var rng = new Random(20260925);
        var sent = new List<byte[]>();
        var noise = Noise(inner).ToList();
        var noiseSent = 0;
        for (var i = 0; i < 3_000; i++)
        {
            if (rng.Next(7) == 0)
            {
                rig.Device.FromWindows(noise[rng.Next(noise.Count)]);
                noiseSent++;
                continue;
            }
            var packet = Game(inner, Servers[rng.Next(Servers.Length)], i, rng.Next(4, 1300));
            sent.Add(packet);
            rig.Device.FromWindows(packet);
        }

        var arrived = await WaitUntil(() => relay.Arrivals.Count >= sent.Count);
        await Task.Delay(100);
        var arrivals = relay.Arrivals.ToList();

        Check($"All {sent.Count:N0} game packets reach the relay", arrived && arrivals.Count == sent.Count,
            $"{arrivals.Count} arrived; the client sent {tunnel.PacketsSent} and counted {tunnel.PacketsDroppedFaults} faults");
        Check("In the order they were sent, each byte for byte what the old loop sent",
            arrivals.Count == sent.Count && arrivals.Select(a => a.Wire)
                .SequenceEqual(sent.Select(p => LegacyWire(p, tunnel.SessionId, inner)!), new BytesEqual()),
            "a datagram on the wire differs from the old loop's");
        Check("None dropped by the relay's anti-spoofing", relay.Spoofed == 0, $"{relay.Spoofed} spoofed");
        Check($"Local noise ({noiseSent}) dropped here, counted as noise and never as a fault",
            tunnel.PacketsDropped == noiseSent && tunnel.PacketsDroppedFaults == 0,
            $"dropped {tunnel.PacketsDropped}, faults {tunnel.PacketsDroppedFaults}");
        Check("PacketsSent and the game-server tally count the game packets",
            tunnel.PacketsSent == sent.Count && tunnel.Destinations.UdpPackets == sent.Count,
            $"sent {tunnel.PacketsSent}, tally {tunnel.Destinations.UdpPackets}");
    }

    /// <summary>Unchanged by Phase A, and checked because the adapter under it is now an interface.</summary>
    private static async Task DownlinkDeliversEveryPacketAndKeepsProbesOut()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 2);
        var inner = U32(tunnel.Session.ClientIp);
        var session = relay.SessionOf(tunnel.SessionId)!;

        var sent = Enumerable.Range(0, 1_000).Select(i => Game(Servers[i % 3], inner, i)).ToList();
        foreach (var packet in sent) relay.SendToClient(session, packet);
        var arrived = await WaitUntil(() => rig.Device.ToWindows.Count >= sent.Count);

        Check("1,000 packets from the relay reach Windows in order, byte for byte",
            arrived && rig.Device.ToWindows.Select(t => t.Packet).SequenceEqual(sent, new BytesEqual()),
            $"{rig.Device.ToWindows.Count} arrived");
        Check("PacketsReceived counts them", tunnel.PacketsReceived == sent.Count, $"{tunnel.PacketsReceived}");

        var before = rig.Device.ToWindows.Count;
        var rtt = await tunnel.ProbeGameServerAsync(new IPAddress([43, 132, 208, 47]), 800, rig.Cts.Token);
        await Task.Delay(100);
        Check("An echo through the live tunnel is answered, and its reply is consumed - never handed to Windows",
            rtt is not null && rig.Device.ToWindows.Count == before,
            $"rtt {rtt?.ToString("F1") ?? "none"}, {rig.Device.ToWindows.Count - before} extra packets reached Windows");

        // The in-game ping and the region planner can each have an echo out through one tunnel at the same moment
        // (MULTI-TUNNEL.md 5.6). With one slot the second came back null - "no answer through this tunnel".
        var together = await Task.WhenAll(
            tunnel.ProbeGameServerAsync(new IPAddress([43, 132, 208, 47]), 800, rig.Cts.Token),
            tunnel.ProbeGameServerAsync(new IPAddress([34, 146, 241, 71]), 800, rig.Cts.Token),
            tunnel.ProbeGameServerAsync(new IPAddress([43, 132, 208, 47]), 800, rig.Cts.Token));
        await Task.Delay(100);
        Check("Three echoes out at once through one tunnel are each answered, and none reaches Windows",
            together.All(r => r is not null) && rig.Device.ToWindows.Count == before,
            $"answered {together.Count(r => r is not null)} of 3, {rig.Device.ToWindows.Count - before} reached Windows");
    }

    private static async Task KeepaliveFindsTheRelay()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 3);
        await Task.Delay(2_600);
        Check("A ping a second, answered, round trip measured, the tunnel heard from",
            Interlocked.Read(ref relay.Pings) >= 2 && tunnel.LastRttMs is not null && tunnel.SinceLastHeard < TimeSpan.FromSeconds(2),
            $"pings {relay.Pings}, rtt {tunnel.LastRttMs}, heard {tunnel.SinceLastHeard.TotalSeconds:F1} s ago");
    }

    /// <summary>
    /// What a reconnect, a failover and a move between matches do to the pump: target cleared, old tunnel put
    /// away, new one pointed at. Packets sent in between must wait in the adapter and leave through the new
    /// tunnel - which is what the old per-tunnel threads did by stopping and starting.
    /// </summary>
    private static async Task ASwapLeavesPacketsWaitingInTheAdapter()
    {
        using var relayA = new FakeRelay(Psk, firstInner: 2);
        using var relayB = new FakeRelay(Psk, firstInner: 2);
        using var rig = new Rig();
        var a = await rig.StartAsync(relayA, clientId: 4);
        var inner = U32(a.Session.ClientIp);

        rig.Pump.SetHome(null);
        a.AnnounceDisconnect = false;
        a.Dispose();

        var waiting = Enumerable.Range(0, 50).Select(i => Game(inner, Servers[0], i)).ToList();
        foreach (var packet in waiting) rig.Device.FromWindows(packet);
        await Task.Delay(600);
        Check("With no tunnel, the adapter is not read: all 50 packets wait in it",
            rig.Device.Waiting == 50 && relayA.Arrivals.IsEmpty, $"{rig.Device.Waiting} waiting, {relayA.Arrivals.Count} reached the old relay");

        var b = await rig.OpenAsync(relayB, clientId: 4);
        b.StartPumping(rig.Device, rig.Cts.Token);
        rig.Pump.SetHome(b);
        var arrived = await WaitUntil(() => relayB.Arrivals.Count >= 50, 3_000);
        Check("The new tunnel takes them, in order, and none was lost in the swap",
            arrived && relayB.Arrivals.Select(x => SequenceOf(x.Inner)).SequenceEqual(Enumerable.Range(0, 50)) && rig.Pump.DroppedNoTarget == 0,
            $"{relayB.Arrivals.Count} arrived, {rig.Pump.DroppedNoTarget} dropped with no target");

        // Not new, and worth knowing: a relay that hands out a DIFFERENT inner address drops what waited, because
        // it was written with the old one. Before Phase A too (the adapter is re-addressed after the swap). The
        // fixed adapter address of multi-tunnel mode - rewritten per tunnel - is what ends it (MULTI-TUNNEL 5.2).
        using var relayC = new FakeRelay(Psk, firstInner: 7);
        rig.Pump.SetHome(null);
        b.AnnounceDisconnect = false;
        b.Dispose();
        foreach (var packet in waiting) rig.Device.FromWindows(packet);
        var c = await rig.OpenAsync(relayC, clientId: 4);
        c.StartPumping(rig.Device, rig.Cts.Token);
        rig.Pump.SetHome(c);
        await WaitUntil(() => Interlocked.Read(ref relayC.Spoofed) >= 50, 3_000);
        Check("As before Phase A: to a relay with another inner address, what waited is dropped there (50 of 50)",
            Interlocked.Read(ref relayC.Spoofed) == 50 && relayC.Arrivals.IsEmpty, $"{relayC.Spoofed} dropped, {relayC.Arrivals.Count} passed");
    }

    /// <summary>
    /// Entry switching moves the socket under the pump while the game sends 150 packets a second each way. The
    /// session, the inner address and so the game server's view must not change, and nothing but what was in
    /// flight on loopback may be lost.
    /// </summary>
    private static async Task AMoveToAnotherDoorUnderLoadLosesNothing()
    {
        using var relay = new FakeRelay(Psk, doors: 2);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 5, door: 0);
        var inner = U32(tunnel.Session.ClientIp);
        var session = relay.SessionOf(tunnel.SessionId)!;
        var doors = relay.Ports;

        var up = 0;
        var down = 0;
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var moved = Task.Run(async () =>
        {
            await Task.Delay(1_500);
            tunnel.MoveTo(relay.Door(1));
        });
        // Paced against the clock, not by Task.Delay alone: Windows' timer sleeps ~15 ms for a 6 ms delay, which
        // would test a third of the rate claimed.
        var started = Stopwatch.GetTimestamp();
        while (!stop.IsCancellationRequested)
        {
            var due = (int)(Stopwatch.GetElapsedTime(started).TotalSeconds * 150);
            while (up < due)
            {
                rig.Device.FromWindows(Game(inner, Servers[1], up++));
                relay.SendToClient(session, Game(Servers[1], inner, down++));
            }
            await Task.Delay(1);
        }
        await moved;
        await Task.Delay(200);

        var arrivals = relay.Arrivals.ToList();
        var lostUp = up - arrivals.Count;
        var lostDown = down - rig.Device.ToWindows.Count;
        // What MoveTo promises (unchanged by Phase A): whatever was already coming back down the old way is lost - a
        // round trip's worth. On loopback that is the relay's next send, and this loop sends in bursts of Windows'
        // ~15 ms timer tick, so the bound is time, not a packet count: no more than 30 ms of the stream.
        var upMs = lostUp * 1000.0 / 150;
        var downMs = lostDown * 1000.0 / 150;
        Check($"Uplink: {up} sent across the move at {up / 3.0:F0}/s, {lostUp} lost ({upMs:F0} ms of the stream)",
            upMs <= 30 && up >= 400, $"{lostUp} lost");
        Check($"Downlink: {down} sent across the move at {down / 3.0:F0}/s, {lostDown} lost ({downMs:F0} ms of the stream)",
            downMs <= 30, $"{lostDown} lost");
        Check("One session throughout - the game server sees nothing change",
            arrivals.All(a => a.SessionId == tunnel.SessionId) && relay.Handshakes == 1 && relay.Spoofed == 0,
            $"{relay.Handshakes} handshakes, {relay.Spoofed} spoofed");
        Check("After the move, the uplink arrives by the other door",
            arrivals.Count > 0 && arrivals[^1].Port == doors[1] && arrivals[0].Port == doors[0], "the door did not change");
        Check("In order across the move", arrivals.Select(a => SequenceOf(a.Inner)).Zip(arrivals.Skip(1).Select(a => SequenceOf(a.Inner))).All(p => p.First < p.Second),
            "a packet overtook another");
    }

    private static async Task AReconnectResumesTheSession()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var first = await rig.StartAsync(relay, clientId: 6);

        rig.Pump.SetHome(null);
        first.AnnounceDisconnect = false;
        first.Dispose();

        var second = await rig.StartAsync(relay, clientId: 6);
        var inner = U32(second.Session.ClientIp);
        for (var i = 0; i < 20; i++) rig.Device.FromWindows(Game(inner, Servers[2], i));
        var arrived = await WaitUntil(() => relay.Arrivals.Count >= 20, 3_000);

        Check("Abandoned without a Disconnect and handshaken again: the same session and inner address",
            second.SessionId == first.SessionId && second.Session.ClientIp.Equals(first.Session.ClientIp) && relay.Disconnects == 0,
            $"session {first.SessionId:X} -> {second.SessionId:X}, disconnects {relay.Disconnects}");
        Check("And the pump carries on through it", arrived, $"{relay.Arrivals.Count} of 20 arrived");
    }

    /// <summary>
    /// The engine's teardown order - reader, tunnel, session - under load, and nothing touches the ring after the
    /// session ends. Then the wrong order, to show the check would see it: without this, a clean result could just
    /// mean the counter never counts.
    /// </summary>
    private static async Task TeardownNeverTouchesAnEndedSession()
    {
        foreach (var rightOrder in new[] { true, false })
        {
            using var relay = new FakeRelay(Psk);
            var device = new FakeDevice();
            var pump = new AdapterPump(device, s => Log.Enqueue(s));
            pump.Start();
            using var cts = new CancellationTokenSource();
            var tunnel = new TunnelClient(relay.Door(), TunnelAuth.FromPsk(Psk), 7, s => Log.Enqueue(s));
            await tunnel.HandshakeAsync(3, cts.Token);
            tunnel.StartPumping(device, cts.Token);
            pump.SetHome(tunnel);
            var inner = U32(tunnel.Session.ClientIp);
            var session = relay.SessionOf(tunnel.SessionId)!;

            using var load = new CancellationTokenSource();
            var feeding = Task.Run(async () =>
            {
                var i = 0;
                while (!load.IsCancellationRequested)
                {
                    device.FromWindows(Game(inner, Servers[0], i));
                    relay.SendToClient(session, Game(Servers[0], inner, i++));
                    await Task.Delay(1);
                }
            });
            await Task.Delay(300);

            if (rightOrder)
            {
                pump.Dispose();
                tunnel.Dispose();
                device.EndSession();
            }
            else
            {
                device.EndSession();
                await Task.Delay(300);
                pump.Dispose();
                tunnel.Dispose();
            }
            await Task.Delay(300);
            load.Cancel();
            await feeding;

            if (rightOrder)
            {
                Check("Reader, tunnel, then the session, under load: nothing touches the ring after it ends",
                    device.CallsAfterEnd == 0, $"{device.CallsAfterEnd} calls after the end");
            }
            else
            {
                Check("Control: ending the session first IS seen (so the check above can fail)",
                    device.CallsAfterEnd > 0, "the fake device did not notice");
            }
        }
    }

    private static async Task Faults()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 8);
        var inner = U32(tunnel.Session.ClientIp);

        // A packet the adapter hands over whole but too big to wrap: the Data header does not fit behind it. The old
        // loop ended on this, leaving a tunnel that answered keepalives and carried nothing up.
        var tooBig = Game(inner, Servers[0], -1, payload: 2048 - 28);
        rig.Device.FromWindows(tooBig);
        for (var i = 0; i < 100; i++) rig.Device.FromWindows(Game(inner, Servers[0], i));
        var arrived = await WaitUntil(() => relay.Arrivals.Count >= 100, 3_000);
        Check("A packet that cannot be wrapped costs itself and is counted as a fault; the uplink carries on",
            arrived && tunnel.PacketsDroppedFaults == 1, $"{relay.Arrivals.Count} of 100 arrived, faults {tunnel.PacketsDroppedFaults}");

        // Larger than the read buffer: the adapter reports 0, as Wintun does.
        rig.Device.FromWindows(new byte[3_000]);
        rig.Device.FromWindows(Game(inner, Servers[0], 100));
        await WaitUntil(() => relay.Arrivals.Count >= 101, 3_000);
        Check("A packet larger than the read buffer is counted as oversize, and the next one goes",
            tunnel.PacketsDroppedFaults == 2 && relay.Arrivals.Count == 101, $"faults {tunnel.PacketsDroppedFaults}, arrivals {relay.Arrivals.Count}");

        // Windows falling behind: the downlink drops and counts, never blocks.
        var session = relay.SessionOf(tunnel.SessionId)!;
        rig.Device.RingFull = true;
        for (var i = 0; i < 50; i++) relay.SendToClient(session, Game(Servers[0], inner, i));
        await WaitUntil(() => tunnel.PacketsDroppedFaults >= 52, 3_000);
        rig.Device.RingFull = false;
        relay.SendToClient(session, Game(Servers[0], inner, 50));
        var delivered = await WaitUntil(() => rig.Device.ToWindows.Count == 1, 3_000);
        Check("A full adapter ring drops and counts on the downlink, and delivery resumes when it drains",
            tunnel.PacketsDroppedFaults == 52 && delivered, $"faults {tunnel.PacketsDroppedFaults}, delivered {rig.Device.ToWindows.Count}");
    }

    /// <summary>
    /// What the pump adds between Windows handing a packet over and the relay having it, on loopback: one read,
    /// one lookup, one send. Reported, and failed only if it is out of all proportion to a game's timing.
    /// </summary>
    private static async Task LatencyAddedByThePump()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 9);
        var inner = U32(tunnel.Session.ClientIp);

        const int count = 3_000;
        var handedIn = new long[count];
        for (var i = 0; i < count; i++)
        {
            handedIn[i] = Stopwatch.GetTimestamp();
            rig.Device.FromWindows(Game(inner, Servers[0], i));
            if (i % 3 == 0) await Task.Delay(1);
        }
        await WaitUntil(() => relay.Arrivals.Count >= count);

        var ms = relay.Arrivals.Select(a => (a.At - handedIn[SequenceOf(a.Inner)]) * 1000.0 / Stopwatch.Frequency).Order().ToList();
        var p50 = ms[ms.Count / 2];
        var p99 = ms[(int)(ms.Count * 0.99)];
        Check($"Windows to relay on loopback, {count:N0} packets: p50 {p50:F3} ms, p99 {p99:F3} ms", ms.Count == count && p99 < 5,
            $"{ms.Count} arrived, p99 {p99:F3} ms");
    }

    private sealed class BytesEqual : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }
}
