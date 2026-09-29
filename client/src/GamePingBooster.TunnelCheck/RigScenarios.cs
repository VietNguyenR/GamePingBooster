using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The same checks against REAL relays: two relayd in WSL, an entry in front of each by iptables DNAT - as the VN boxes
/// are built - two echoing game servers, and tc netem making one way into a relay slow or lossy. What the fake relay
/// cannot show: relayd's own answer to a Probe (no return-address move, G5), its roaming when a tunnel moves onto an
/// entry, conntrack carrying the replies back through the DNAT, and delay and loss made by a kernel rather than by a
/// Task.Delay.
///
///     wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh up
///     dotnet run --project client/src/GamePingBooster.TunnelCheck -- rig
///
/// No driver, no route and no Administrator: the virtual adapter is TunnelCheck's FakeDevice, as everywhere else here.
/// The service's own part - turning a decision into a move on the right tunnel, pinning, undoing - is the live service
/// run's (docs/MULTI-TUNNEL-HANDOFF.md, "Entry switching on a region's tunnel").
/// </summary>
internal static partial class Program
{
    private static readonly byte[] RigPsk = "gpb-multi-tunnel-rig-psk-0123456789abcdef"u8.ToArray();
    private static readonly uint RigSg = Addr(198, 51, 100, 10);
    private static readonly uint RigKr = Addr(198, 51, 100, 20);
    private const ushort RigPort = 27015;

    private static string _rigScript = "";
    private static IPEndPoint _a = null!, _b = null!, _e = null!, _f = null!;

    private static int RigMain(string[] args)
    {
        var repo = FindRepo();
        _rigScript = Path.Combine(repo, "tools", "multi-tunnel-rig", "rig.sh");
        var status = RigShell("status");
        var a = System.Text.RegularExpressions.Regex.Match(status, @"A=(\S+):51820");
        var b = System.Text.RegularExpressions.Regex.Match(status, @"B=(\S+):51820");
        var e = System.Text.RegularExpressions.Regex.Match(status, @"E=(\S+):51820");
        var f = System.Text.RegularExpressions.Regex.Match(status, @"F=(\S+):51820");
        if (!a.Success || !status.Contains("relayd running: 2"))
        {
            Console.WriteLine("The rig is not up. Run: wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh up");
            Console.WriteLine(status);
            return 2;
        }
        _a = new IPEndPoint(IPAddress.Parse(a.Groups[1].Value), 51820);
        _b = new IPEndPoint(IPAddress.Parse(b.Groups[1].Value), 51820);
        _e = new IPEndPoint(IPAddress.Parse(e.Groups[1].Value), 51820);
        _f = new IPEndPoint(IPAddress.Parse(f.Groups[1].Value), 51820);
        Console.WriteLine($"Rig: A {_a}, F {_f} (entry to A); B {_b}, E {_e} (entry to B).");

        if (args.Length > 0 && args[0] == "debug")
        {
            RigDebug().GetAwaiter().GetResult();
            return 0;
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"gpb-rigcheck-quality-{Environment.ProcessId}");
        QualityFile.DirectoryOverride = scratch;
        var scenarios = new (string Title, Func<Task> Run)[]
        {
            ("Real relayd: loss is measured", RigLossIsMeasured),
            ("Real relayd: Probes down every way never take the session", RigProbesNeverTakeTheSession),
            ("Real relayd: a region's tunnel leaves a slow road mid-match", RigRegionTunnelLeavesASlowRoad),
            ("Real relayd: a region's tunnel leaves a lossy road mid-match", RigRegionTunnelLeavesALossyRoad),
            ("Real relayd: home still leaves its own slow road", RigHomeLeavesItsSlowRoad),
        };
        try
        {
            foreach (var (title, run) in scenarios)
            {
                Console.WriteLine();
                Console.WriteLine($"{title}:");
                RigShell("heal");
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
        }
        finally
        {
            RigShell("heal");
            QualityFile.DirectoryOverride = null;
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All rig checks passed.");
            return 0;
        }
        Console.WriteLine($"{_failures} rig check(s) FAILED. The client's log from the run:");
        foreach (var line in Log.TakeLast(60)) Console.WriteLine($"    {line}");
        Console.WriteLine(RigShell("logs 30"));
        return 1;
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "multi-tunnel-rig", "rig.sh"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("No tools/multi-tunnel-rig/rig.sh above the build output.");
    }

    /// <summary>Runs rig.sh inside WSL as root and returns what it printed.</summary>
    private static string RigShell(string arguments)
    {
        var wslPath = "/mnt/" + char.ToLowerInvariant(_rigScript[0]) + _rigScript[2..].Replace('\\', '/');
        var start = new ProcessStartInfo("wsl.exe", $"-d Ubuntu -u root -- bash {wslPath} {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output.Replace("\0", "");
    }

    private static int BConnects() =>
        System.Text.RegularExpressions.Regex.Matches(RigShell("logs 100000"), @"client connected\S* .*inner_ip=10\.78\.").Count;

    private static int RoamCount() =>
        System.Text.RegularExpressions.Regex.Matches(RigShell("logs 100000"), "client roamed").Count;

    private static byte[] RigPacket(uint src, uint dst, int sequence)
    {
        var packet = Game(src, dst, sequence);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), RigPort);
        var sum = UdpSum(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26), sum == 0 ? (ushort)0xFFFF : sum);
        return packet;
    }

    private static RegionTable RigRegions() => RegionTable.Build([("sg", ["198.51.100.10/32"]), ("kr", ["198.51.100.20/32"])]);

    private static async Task<TunnelClient> RigOpenAsync(Rig rig, IPEndPoint way, ulong clientId)
    {
        var tunnel = new TunnelClient(way, TunnelAuth.FromPsk(RigPsk), clientId, s => Log.Enqueue(s));
        await tunnel.HandshakeAsync(attempts: 3, rig.Cts.Token);
        rig.Track(tunnel);
        return tunnel;
    }

    /// <summary>Home to A, kr's tunnel to B - both direct - and the dispatcher, as the engine has them once a plan is in force.</summary>
    private static async Task<(TunnelClient Home, TunnelClient Other, uint AdapterIp)> RigTunnelsAsync(Rig rig, ulong clientId)
    {
        var home = await RigOpenAsync(rig, _a, clientId);
        home.StartPumping(rig.Device, rig.Cts.Token);
        rig.Pump.SetHome(home);
        var adapterIp = home.InnerIp;
        var dispatcher = new PathDispatcher(adapterIp, RigRegions(), home);
        home.Dispatcher = dispatcher;
        var other = await RigOpenAsync(rig, _b, clientId);
        other.Dispatcher = dispatcher;
        other.StartPumping(rig.Device, rig.Cts.Token);
        rig.Pump.SetDispatcher(dispatcher);
        dispatcher.SetPlan(RigRegions().RegionIdAt(0) == "sg" ? [null, other] : [other, null]);
        return (home, other, adapterIp);
    }

    /// <summary>A game: numbered UDP to both servers, the replies counted by number. Stop it, then read it.</summary>
    private sealed class RigGame
    {
        public int SgSent, KrSent;
        public volatile bool Kr = true;
        public readonly ConcurrentDictionary<int, long> SgBack = new(), KrBack = new();
        private readonly Task _sending, _receiving;
        private readonly CancellationTokenSource _stop;

        public RigGame(Rig rig, uint adapterIp, CancellationToken ct, int krEveryMs = 25, int sgEvery = 3)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _stop.Token;
            _sending = Task.Run(async () =>
            {
                var tick = 0;
                while (!token.IsCancellationRequested)
                {
                    if (Kr) rig.Device.FromWindows(RigPacket(adapterIp, RigKr, KrSent++));
                    if (tick++ % sgEvery == 0 || !Kr) rig.Device.FromWindows(RigPacket(adapterIp, RigSg, SgSent++));
                    await Task.Delay(krEveryMs);
                }
            });
            _receiving = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    while (rig.Device.ToWindows.TryDequeue(out var p))
                    {
                        if (p.Packet.Length < 32 || p.Packet[9] != 17) continue;
                        var from = Src(p.Packet);
                        if (from == RigKr) KrBack.TryAdd(SequenceOf(p.Packet), p.At);
                        else if (from == RigSg) SgBack.TryAdd(SequenceOf(p.Packet), p.At);
                    }
                    await Task.Delay(5);
                }
            });
        }

        public async Task StopAsync()
        {
            _stop.Cancel();
            await Task.WhenAll(_sending, _receiving);
            await Task.Delay(400);
        }

        /// <summary>Replies missing among the numbers sent from <paramref name="from"/> on, the last second's still in flight left out.</summary>
        public static int Missing(ConcurrentDictionary<int, long> back, int from, int sent) =>
            Enumerable.Range(from, Math.Max(0, sent - 40 - from)).Count(i => !back.ContainsKey(i));
    }

    // ------------------------------------------------------------ scenarios

    private static async Task RigDebug()
    {
        using var rig = new Rig();
        var home = await RigOpenAsync(rig, _a, 399);
        home.StartPumping(rig.Device, rig.Cts.Token);
        rig.Pump.SetHome(home);
        for (var i = 0; i < 5; i++) rig.Device.FromWindows(RigPacket(home.InnerIp, RigSg, i));
        await Task.Delay(1500);
        Console.WriteLine($"sent 5, home sent {home.PacketsSent} recv {home.PacketsReceived}, ToWindows {rig.Device.ToWindows.Count}, drops {home.PacketsDroppedFaults}");
        foreach (var (p, _) in rig.Device.ToWindows.Take(5))
            Console.WriteLine($"  len {p.Length} proto {p[9]} src {new IPAddress(p.AsSpan(12, 4))} dst {new IPAddress(p.AsSpan(16, 4))} seq {(p.Length >= 32 ? SequenceOf(p) : -1)}");
    }

    private static async Task RigLossIsMeasured()
    {
        using var rig = new Rig();
        var clean = await RigOpenAsync(rig, _b, 301);
        var (_, cleanLoss) = await clean.MeasureRelayBurstAsync(CancellationToken.None);
        Check($"B direct, clean: {cleanLoss}", !cleanLoss.IsLossy && cleanLoss.Lost == 0, cleanLoss.ToString());

        RigShell("degrade b 0 25");
        var lossy = await RigOpenAsync(rig, _b, 302);
        var lossyLoss = PingLoss.Unknown;
        for (var attempt = 0; attempt < 3 && !lossyLoss.IsLossy; attempt++) (_, lossyLoss) = await lossy.MeasureRelayBurstAsync(CancellationToken.None);
        Check($"B direct, 25% dropped by netem: {lossyLoss}, lossy", lossyLoss.IsLossy, lossyLoss.ToString());

        var entry = await RigOpenAsync(rig, _e, 303);
        var (_, entryLoss) = await entry.MeasureRelayBurstAsync(CancellationToken.None);
        Check($"E, the entry to the same relay, meanwhile: {entryLoss}, clean - the shaping is per way", !entryLoss.IsLossy, entryLoss.ToString());
    }

    private static async Task RigProbesNeverTakeTheSession()
    {
        using var rig = new Rig();
        var (_, other, adapterIp) = await RigTunnelsAsync(rig, 310);
        var game = new RigGame(rig, adapterIp, rig.Cts.Token);
        await Task.Delay(1500);
        var roamedBefore = RoamCount();

        RigShell("degrade b 60");
        await Task.Delay(500);
        var fromSeq = game.KrSent;
        var samples = await TunnelEngine.ProbeWaysAsync(other.SessionId,
            [new DoorProbes.Door("b", _b), new DoorProbes.Door("e", _e)], WayCheck.Rounds, WayCheck.SpacingFor(2), CancellationToken.None);
        var choice = WayCheck.Choose("b", samples);
        await Task.Delay(1500);
        await game.StopAsync();

        Check($"Both ways by Probe, B's road 60 ms slow: {string.Join(", ", samples)} - {choice.Reason}",
            choice.MoveTo == "e" && samples[0].MedianMs > 50 && samples[1].MedianMs < 20, choice.Reason);
        var missing = RigGame.Missing(game.KrBack, fromSeq, game.KrSent);
        Check($"  and Korea's game kept every reply on the tunnel while its relay was probed down both ways ({game.KrSent - fromSeq} sent)",
            missing == 0, $"{missing} missing");
        Check("  relayd never moved the session: no 'client roamed' while it was probed", RoamCount() == roamedBefore,
            $"{RoamCount() - roamedBefore} roam(s)");
    }

    private static Task RigRegionTunnelLeavesASlowRoad() => RigRegionTunnelLeaves("degrade b 80", "80 ms slow", 320, slowOnly: true);

    private static Task RigRegionTunnelLeavesALossyRoad() => RigRegionTunnelLeaves("degrade b 0 20", "losing a fifth", 330, slowOnly: false);

    /// <summary>
    /// A match on kr's tunnel, its direct road to B made bad; the recorder follows the tunnel and its policy for B moves it
    /// onto E. Home's game on A is watched throughout and must lose nothing and never move.
    /// </summary>
    /// <param name="slowOnly">The road is slow but loses nothing: then not one reply may be missing, from the first to the
    /// last - the ones in flight down the slow road at the move included (TunnelClient's drain). A lossy road loses its own
    /// share before the move, so only what came after it is counted.</param>
    private static async Task RigRegionTunnelLeaves(string degrade, string what, ulong clientId, bool slowOnly)
    {
        using var rig = new Rig();
        var (home, other, adapterIp) = await RigTunnelsAsync(rig, clientId);
        var gate = new object();
        string? otherEntry = null;
        IReadOnlyList<DoorProbes.Door> otherDoors = [new("e", _e)];
        IReadOnlyList<DoorProbes.Door> homeDoors = [new("f", _f)];
        var decisions = new ConcurrentQueue<DoorDecision>();
        var carrier = new MatchCarrier<TunnelClient>();

        SpikeRecorder.Context Context()
        {
            lock (gate)
            {
                return ReferenceEquals(carrier.Current, other)
                    ? new SpikeRecorder.Context(other, "b", otherEntry, "B", null, null, null, "rig", true, otherDoors, true, Carried: "other")
                    : new SpikeRecorder.Context(home, "a", null, "A", null, null, null, "rig", true, homeDoors, true, Carried: "home");
            }
        }
        bool Move(DoorDecision decision)
        {
            decisions.Enqueue(decision);
            if (decision.To != "e") return false;
            lock (gate)
            {
                other.MoveTo(_e);
                otherEntry = "e";
                otherDoors = [new("b", _b)];
            }
            return true;
        }

        var game = new RigGame(rig, adapterIp, rig.Cts.Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(rig.Cts.Token);
        var recorder = new SpikeRecorder(Context, s => Log.Enqueue(s), Move);
        var running = Task.Run(() => recorder.RunAsync(stop.Token));
        var drive = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                carrier.Update(Environment.TickCount64, home, home.Destinations.UdpPackets, [(other, other.Destinations.UdpPackets)]);
                await Task.Delay(1000);
            }
        });

        await Task.Delay(4000);
        var connectsBefore = BConnects();
        Check("The match on kr's tunnel is the carrier", ReferenceEquals(carrier.Current, other));
        RigShell(degrade);
        var started = Environment.TickCount64;
        var moved = await WaitUntil(() => decisions.Any(d => d.To == "e"), 50_000);
        var seconds = (Environment.TickCount64 - started) / 1000.0;
        var afterMove = game.KrSent;
        Check($"B's direct road {what}: the policy for B moves kr's tunnel onto E ({seconds:F0} s)",
            moved && decisions.First(d => d.To == "e").From == "b", string.Join("; ", decisions.Select(d => $"{d.From}->{d.To}")));

        await Task.Delay(6000);
        stop.Cancel();
        await Task.WhenAll(running, drive);
        await game.StopAsync();

        var countFrom = slowOnly ? 40 : afterMove + 40;
        var krMissing = RigGame.Missing(game.KrBack, countFrom, game.KrSent);
        Check(slowOnly
                ? $"  not one of Korea's replies missing, the ones in flight down the slow road at the move included ({game.KrSent - 80} counted)"
                : $"  after the move every one of Korea's replies comes back, down E ({game.KrSent - countFrom - 40} counted)",
            krMissing == 0, $"{krMissing} missing");
        var sgMissing = RigGame.Missing(game.SgBack, 40, game.SgSent);
        Check($"  home's game on A lost nothing throughout ({game.SgSent} sent) and home never moved",
            sgMissing == 0 && !decisions.Any(d => d.From == "a"), $"{sgMissing} missing");
        Check("  the tunnel is on E, and relayd B kept its one session - no new handshake, the same inner address",
            other.Endpoint.Equals(_e) && BConnects() == connectsBefore, $"endpoint {other.Endpoint}, {BConnects() - connectsBefore} new session(s)");
    }

    private static async Task RigHomeLeavesItsSlowRoad()
    {
        using var rig = new Rig();
        var home = await RigOpenAsync(rig, _a, 340);
        home.StartPumping(rig.Device, rig.Cts.Token);
        rig.Pump.SetHome(home);
        var gate = new object();
        string? entry = null;
        IReadOnlyList<DoorProbes.Door> doors = [new("f", _f)];
        var decisions = new ConcurrentQueue<DoorDecision>();
        SpikeRecorder.Context Context()
        {
            lock (gate) return new SpikeRecorder.Context(home, "a", entry, "A", null, null, null, "rig", true, doors, true);
        }
        bool Move(DoorDecision decision)
        {
            decisions.Enqueue(decision);
            if (decision.To != "f") return false;
            lock (gate)
            {
                home.MoveTo(_f);
                entry = "f";
                doors = [new("a", _a)];
            }
            return true;
        }

        var sent = 0;
        var back = new ConcurrentDictionary<int, bool>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(rig.Cts.Token);
        var recorder = new SpikeRecorder(Context, s => Log.Enqueue(s), Move);
        var running = Task.Run(() => recorder.RunAsync(stop.Token));
        var sending = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                rig.Device.FromWindows(RigPacket(home.InnerIp, RigSg, sent++));
                while (rig.Device.ToWindows.TryDequeue(out var p)) if (p.Packet.Length >= 32 && Src(p.Packet) == RigSg) back.TryAdd(SequenceOf(p.Packet), true);
                await Task.Delay(25);
            }
        });

        await Task.Delay(3000);
        RigShell("degrade a 80");
        var moved = await WaitUntil(() => decisions.Any(d => d.To == "f"), 50_000);
        var afterMove = sent;
        await Task.Delay(5000);
        stop.Cancel();
        await Task.WhenAll(running, sending);
        Check("Home's road to A 80 ms slow: home's policy moves it onto F, as before this change", moved,
            string.Join("; ", decisions.Select(d => $"{d.From}->{d.To}")));
        var missing = Enumerable.Range(40, Math.Max(0, sent - 80)).Count(i => !back.ContainsKey(i));
        Check("  not one reply missing, across the move and the ones in flight at it", missing == 0, $"{missing} missing");
    }
}
