using GamePingBooster.Core.Paths;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Phase D2: the spike recorder follows the tunnel carrying the match (docs/MULTI-TUNNEL.md 5.8). The engine hands
/// the recorder a context naming <see cref="MatchCarrier{TTunnel}"/>'s answer; this drives the same pair with real
/// tunnels and a match on the second relay.
///
/// Stopped well short of the recorder's 30 s minimum match and with the game's stream never broken, so it writes
/// nothing into this PC's quality folder or upload queue.
/// </summary>
internal static partial class Program
{
    private static async Task TheRecorderFollowsTheTunnelCarryingTheMatch()
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 46);
        m.KrViaOther();

        var carrier = new MatchCarrier<TunnelClient>();
        void Drive() => carrier.Update(Environment.TickCount64, m.Home, m.Home.Destinations.UdpPackets,
            [(m.Other, m.Other.Destinations.UdpPackets)]);
        SpikeRecorder.Context Context()
        {
            var onOther = ReferenceEquals(carrier.Current, m.Other);
            return new SpikeRecorder.Context(onOther ? m.Other : m.Home, onOther ? "hk-2" : "sg-2", null, onOther ? "HK 2" : "SG 2",
                null, null, null, "tunnelcheck", GameRunning: true, [], MovesEnabled: false, Carried: onOther ? "other" : "home");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(m.Rig.Cts.Token);
        var recorder = new SpikeRecorder(Context, line => Log.Enqueue(line));
        var running = Task.Run(() => recorder.RunAsync(stop.Token));

        Drive();
        await Task.Delay(600);
        Check("Before any match the recorder listens to home, and to nothing else",
            ReferenceEquals(m.Home.QualitySink, recorder) && m.Other.QualitySink is null && !recorder.InMatch);

        // A match in Korea: 40 packets a second, out by the other relay, for five seconds.
        var sequence = 0;
        var sending = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && sequence < 200)
            {
                m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, KrServer, sequence++));
                await Task.Delay(25);
            }
        });
        var tookOver = false;
        for (var second = 0; second < 5 && !tookOver; second++)
        {
            await Task.Delay(1_000);
            Drive();
            tookOver = ReferenceEquals(carrier.Current, m.Other);
        }
        Check("The match on the other relay becomes the carrier", tookOver, $"after {sequence} packets");

        var followed = await WaitUntil(() => ReferenceEquals(m.Other.QualitySink, recorder) && m.Home.QualitySink is null, 2_000);
        Check("The recorder moves to it and lets go of home - pongs and echoes come from the tunnel carrying the match",
            followed, $"home sink {m.Home.QualitySink is not null}, other sink {m.Other.QualitySink is not null}");
        var inMatch = await WaitUntil(() => recorder.InMatch, 2_000);
        Check("and it records the match there", inMatch);

        stop.Cancel();
        await sending;
        await running;
        Check("Stopped, it listens to neither", m.Home.QualitySink is null && m.Other.QualitySink is null);
    }
}
