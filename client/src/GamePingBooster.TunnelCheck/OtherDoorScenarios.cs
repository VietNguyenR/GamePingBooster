using System.Collections.Concurrent;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Entry switching on a region's tunnel (2026-09-30): the recorder follows the tunnel carrying the match, its switch
/// policy for THAT relay judges the ways in, and the tunnel moves - with the match's packets flowing on it and home's on
/// home. Then the match moves home and home's own policy moves home at once: a single policy for every tunnel held
/// home's roads still for five minutes after a move on the other one.
///
/// Real tunnels, a real recorder and real Probes; the engine's part - turning a decision into TunnelClient.MoveTo and a
/// new context - is played by the callback, as the supervisor plays it. About 25 s: two eight-second windows and the
/// match moving between them. The recorder writes its match summary into a scratch folder, never this PC's quality
/// folder or upload queue.
/// </summary>
internal static partial class Program
{
    private static async Task ARegionsTunnelMovesItsWayInLikeHome()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"gpb-tunnelcheck-quality-{Environment.ProcessId}");
        QualityFile.DirectoryOverride = scratch;
        try
        {
            await RegionTunnelMoves();
        }
        finally
        {
            QualityFile.DirectoryOverride = null;
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task RegionTunnelMoves()
    {
        // Home: vn-2 with a second way in; the region's tunnel: sg-1, direct road (door 0) and the entry vn-1-sg (door 1).
        using var homeRelay = new FakeRelay(Psk, doors: 2, firstInner: 2);
        using var otherRelay = new FakeRelay(Psk, doors: 2, firstInner: 100);
        using var rig = new Rig();
        var home = await rig.StartAsync(homeRelay, 60);
        var adapterIp = home.InnerIp;
        var dispatcher = new PathDispatcher(adapterIp, TwoRegions(), home);
        home.Dispatcher = dispatcher;
        var other = await rig.OpenAsync(otherRelay, 60);
        other.Dispatcher = dispatcher;
        other.StartPumping(rig.Device, rig.Cts.Token);
        rig.Pump.SetDispatcher(dispatcher);
        dispatcher.SetPlan([null, other]);   // sg stays home, kr leaves by sg-1's tunnel

        // What the engine hands the recorder (SpikeContext): the carrier's relay, the way it is on, the others.
        var gate = new object();
        string? homeEntry = null, otherEntry = null;
        IReadOnlyList<DoorProbes.Door> homeDoors = [new("vn-2-b", homeRelay.Door(1))];
        IReadOnlyList<DoorProbes.Door> otherDoors = [new("vn-1-sg", otherRelay.Door(1))];
        var decisions = new ConcurrentQueue<(DoorDecision Decision, long At)>();
        var carrier = new MatchCarrier<TunnelClient>();

        SpikeRecorder.Context Context()
        {
            lock (gate)
            {
                return ReferenceEquals(carrier.Current, other)
                    ? new SpikeRecorder.Context(other, "sg-1", otherEntry, "SG 1", null, null, null, "tunnelcheck", GameRunning: true,
                        otherDoors, MovesEnabled: true, Carried: "other")
                    : new SpikeRecorder.Context(home, "vn-2", homeEntry, "VN 2", null, null, null, "tunnelcheck", GameRunning: true,
                        homeDoors, MovesEnabled: true, Carried: "home");
            }
        }

        // The supervisor's part: MoveTo the way asked for, and hand over the new context - the way now in use, the others.
        bool Move(DoorDecision decision)
        {
            decisions.Enqueue((decision, Environment.TickCount64));
            lock (gate)
            {
                switch (decision.To)
                {
                    case "vn-1-sg":
                        other.MoveTo(otherRelay.Door(1));
                        otherEntry = "vn-1-sg";
                        otherDoors = [new("sg-1", otherRelay.Door(0))];
                        return true;
                    case "vn-2-b":
                        home.MoveTo(homeRelay.Door(1));
                        homeEntry = "vn-2-b";
                        homeDoors = [new("vn-2", homeRelay.Door(0))];
                        return true;
                }
            }
            return false;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(rig.Cts.Token);
        var recorder = new SpikeRecorder(Context, line => Log.Enqueue(line), Move);
        var running = Task.Run(() => recorder.RunAsync(stop.Token));

        // Two games' worth of traffic: Korea at 40 packets a second (the match, on sg-1's tunnel) and Singapore at 12 on
        // home, both numbered, so a single packet lost to a move shows.
        var krSent = 0;
        var sgSent = 0;
        var krOn = true;
        var sending = Task.Run(async () =>
        {
            var tick = 0;
            while (!stop.IsCancellationRequested)
            {
                if (Volatile.Read(ref krOn)) rig.Device.FromWindows(GameChecked(adapterIp, KrServer, krSent++));
                if (tick++ % 3 == 0 || !Volatile.Read(ref krOn)) rig.Device.FromWindows(GameChecked(adapterIp, SgServer, sgSent++));
                await Task.Delay(25);
            }
        });

        // Downlink on the region's tunnel, every 100 ms from sg-1, to wherever the session's return address is now.
        var krBack = 0;
        var downlink = Task.Run(async () =>
        {
            var session = otherRelay.SessionOf(other.SessionId)!;
            while (!stop.IsCancellationRequested)
            {
                otherRelay.SendToClient(session, GameChecked(KrServer, other.InnerIp, 100_000 + krBack++));
                await Task.Delay(100);
            }
        });

        // The road to sg-1 goes bad: 80 ms more on its direct road, the entry fine.
        otherRelay.DoorDelayMs[0] = 80;
        var started = Environment.TickCount64;
        var drove = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                carrier.Update(Environment.TickCount64, home, home.Destinations.UdpPackets, [(other, other.Destinations.UdpPackets)]);
                await Task.Delay(1_000);
            }
        });

        var movedOther = await WaitUntil(() => decisions.Any(d => d.Decision.To == "vn-1-sg"), 45_000);
        var otherDecision = decisions.FirstOrDefault(d => d.Decision.To == "vn-1-sg");
        Check($"Region's tunnel: its direct road 80 ms slow, the entry fine - the policy for sg-1 moves it to vn-1-sg " +
              $"({(movedOther ? (otherDecision.At - started) / 1000.0 : 0):F0} s: eight seconds of evidence, six of them worse)",
            movedOther && otherDecision.Decision.From == "sg-1", Show(decisions));
        var session1 = otherRelay.SessionOf(other.SessionId);
        Check("  the same session, now answered on the entry's door - the relay and the game server see nothing change",
            session1 is not null && session1.Address is not null && otherRelay.Door(1).Port == ((System.Net.IPEndPoint)session1.Socket!.Client.LocalEndPoint!).Port);
        var home1 = homeRelay.SessionOf(home.SessionId);
        Check("  home not touched: still on its first door, no decision about it",
            home1?.Socket is { } hs && ((System.Net.IPEndPoint)hs.Client.LocalEndPoint!).Port == homeRelay.Door(0).Port &&
            !decisions.Any(d => d.Decision.From.StartsWith("vn-2")));

        // Keep Korea going a little on the new road, then look at every packet.
        var krBackBefore = rig.Device.ToWindows.Count(p => Src(p.Packet) == KrServer);
        await Task.Delay(3_000);
        var krBackAfter = rig.Device.ToWindows.Count(p => Src(p.Packet) == KrServer);
        Check("  replies from Korea keep reaching Windows after the move, down the entry",
            krBackAfter - krBackBefore >= 20, $"{krBackAfter - krBackBefore} in 3 s");

        // The match moves home: Korea stops, Singapore picks up to a match's rate, and home's road goes bad.
        Volatile.Write(ref krOn, false);
        homeRelay.DoorDelayMs[0] = 80;
        var homeStarted = Environment.TickCount64;
        var carriedHome = await WaitUntil(() => ReferenceEquals(carrier.Current, home), 20_000);
        Check("The match moves home: home carries it now", carriedHome);

        var movedHome = await WaitUntil(() => decisions.Any(d => d.Decision.To == "vn-2-b"), 50_000);
        var homeDecision = decisions.FirstOrDefault(d => d.Decision.To == "vn-2-b");
        Check($"Home's road 80 ms slow - home's own policy moves home ({(movedHome ? (homeDecision.At - homeStarted) / 1000.0 : 0):F0} s " +
              "after), not held five minutes by the move just made on sg-1's tunnel",
            movedHome && homeDecision.Decision.From == "vn-2", Show(decisions));

        stop.Cancel();
        await Task.WhenAll(sending, downlink, drove, running);
        await Task.Delay(300);

        // Every packet of both flows reached a relay: nothing lost to either move.
        var krArrived = otherRelay.Arrivals.Where(a => Dst(a.Inner) == KrServer).Select(a => SequenceOf(a.Inner)).ToHashSet();
        var sgArrived = homeRelay.Arrivals.Where(a => Dst(a.Inner) == SgServer).Select(a => SequenceOf(a.Inner)).ToHashSet();
        var krMissing = Enumerable.Range(0, krSent).Count(i => !krArrived.Contains(i));
        var sgMissing = Enumerable.Range(0, sgSent).Count(i => !sgArrived.Contains(i));
        Check($"Korea: all {krSent} packets reached sg-1, across the move to the entry", krMissing == 0, $"{krMissing} missing");
        Check($"Singapore: all {sgSent} packets reached vn-2, across home's move - and none went to sg-1",
            sgMissing == 0 && !otherRelay.Arrivals.Any(a => Dst(a.Inner) == SgServer), $"{sgMissing} missing");
        Check("No packet reached the wrong relay or was taken as spoofed", homeRelay.Spoofed == 0 && otherRelay.Spoofed == 0 &&
            !homeRelay.Arrivals.Any(a => Dst(a.Inner) == KrServer), $"spoofed {homeRelay.Spoofed}/{otherRelay.Spoofed}");
        Check("Exactly two moves: one per tunnel, no flapping", decisions.Count == 2, Show(decisions));
    }

    private static string Show(IEnumerable<(DoorDecision Decision, long At)> decisions) =>
        string.Join("; ", decisions.Select(d => $"{d.Decision.From} -> {d.Decision.To}"));
}
