using System.Diagnostics;
using System.Net;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Lane hunting (LanePick, TunnelEngine.Lanes): the relay answers one source port in four at once and the rest 25 ms
/// late, as the ISP's links did on 2026-09-30. The tunnel starts on a slow port; the hunt must find a fast one, and the
/// move onto that very socket must keep the session and lose no more than a move between ways in does.
/// </summary>
internal static partial class Program
{
    private static bool FastPort(int port) => port % 4 == 0;

    private static async Task ALaneHuntFindsTheFastPortAndMovesOntoIt()
    {
        using var relay = new FakeRelay(Psk);
        relay.LaneDelayMs = port => FastPort(port) ? 0 : 25;
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 40);
        for (var i = 0; i < 40 && FastPort(tunnel.LocalPort); i++) tunnel.MoveTo(tunnel.Endpoint);
        var startPort = tunnel.LocalPort;
        Check("The tunnel starts on a slow port", !FastPort(startPort) && startPort != 0, $"port {startPort}");

        using var hunt = await tunnel.HuntLanesAsync(LanePick.Candidates, LanePick.Rounds, LanePick.MaxProbesPerSecond, rig.Cts.Token);
        Check("A hunt comes back", hunt is not null, "null");
        if (hunt is null) return;

        var fastFound = hunt.Candidates.Count(c => c.MedianMs is < 10);
        Check($"The lane in use measures slow ({hunt.Current}), and fast ports are among the {hunt.Candidates.Count} tried ({fastFound})",
            hunt.Current.MedianMs is > 20 && hunt.Current.Loss.Lost == 0 && fastFound > 0,
            string.Join(", ", hunt.Candidates.Select(c => c.ToString())));
        Check($"{LanePick.Rounds} Probes a lane at {LanePick.MaxProbesPerSecond}/s: {hunt.Seconds:F1} s",
            hunt.Seconds < (LanePick.Candidates + 1) * LanePick.Rounds / (double)LanePick.MaxProbesPerSecond + 2, $"{hunt.Seconds:F1} s");

        var pick = LanePick.Choose(hunt.Current, hunt.Candidates);
        Check("The pick is a fast port", pick.Slot is { } s && hunt.Candidates[s - 1].MedianMs is < 10, pick.Reason);
        if (pick.Slot is not { } slot) return;
        var socket = hunt.Take(slot)!;
        var fastPort = ((IPEndPoint)socket.LocalEndPoint!).Port;

        // The move, while the game sends 150 packets a second each way - as the move between ways in is checked.
        var inner = U32(tunnel.Session.ClientIp);
        var session = relay.SessionOf(tunnel.SessionId)!;
        var handshakes = relay.Handshakes;
        var up = 0;
        var down = 0;
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var moved = Task.Run(async () =>
        {
            await Task.Delay(1_500);
            tunnel.MoveToLane(socket);
        });
        var started = Stopwatch.GetTimestamp();
        while (!stop.IsCancellationRequested)
        {
            var due = (int)(Stopwatch.GetElapsedTime(started).TotalSeconds * 150);
            while (up < due)
            {
                rig.Device.FromWindows(Game(inner, Servers[0], up++));
                relay.SendToClient(session, Game(Servers[0], inner, down++));
            }
            await Task.Delay(1);
        }
        await moved;
        await Task.Delay(200);

        var lostUpMs = (up - relay.Arrivals.Count) * 1000.0 / 150;
        var lostDownMs = (down - rig.Device.ToWindows.Count) * 1000.0 / 150;
        Check($"Across the move: {up} up and {down} down, {lostUpMs:F0} and {lostDownMs:F0} ms of the stream lost",
            lostUpMs <= 30 && lostDownMs <= 30 && up >= 400, $"{up - relay.Arrivals.Count} up, {down - rig.Device.ToWindows.Count} down lost");
        Check("One session, no handshake: the game server sees nothing change",
            relay.Handshakes == handshakes && relay.Arrivals.All(a => a.SessionId == tunnel.SessionId) && relay.Spoofed == 0,
            $"{relay.Handshakes - handshakes} handshakes, {relay.Spoofed} spoofed");
        Check("The tunnel sends from the socket the hunt measured, and the relay answers it there",
            tunnel.LocalPort == fastPort && session.Address?.Port == fastPort, $"tunnel {tunnel.LocalPort}, relay has {session.Address?.Port}, hunted {fastPort}");

        using var after = await tunnel.HuntLanesAsync(0, LanePick.Rounds, LanePick.MaxProbesPerSecond, rig.Cts.Token);
        Check($"Measured again on the tunnel's own socket, the lane taken is fast: {after?.Current}",
            after?.Current.MedianMs is < 10 && after.Current.Loss.Lost == 0, $"{after?.Current}");
        await Task.Delay(1_200);
        Check("The pongs follow: the keepalive's round trip is the fast lane's", tunnel.LastRttMs is < 10, $"{tunnel.LastRttMs:F1} ms");
    }

    private static async Task ALineWithoutLanesIsLeftAlone()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 41);
        var port = tunnel.LocalPort;

        using var hunt = await tunnel.HuntLanesAsync(LanePick.Candidates, LanePick.Rounds, LanePick.MaxProbesPerSecond, rig.Cts.Token);
        var pick = hunt is null ? null : LanePick.Choose(hunt.Current, hunt.Candidates);
        Check("Every port the same: nothing to take", pick is { Slot: null }, pick?.Reason ?? "no hunt");
        Check("And every candidate answered every Probe - the hunt itself loses nothing",
            hunt is not null && hunt.Current.Loss.Lost == 0 && hunt.Candidates.All(c => c.Loss.Lost == 0 && c.Loss.Sent == LanePick.Rounds),
            hunt is null ? "no hunt" : string.Join(", ", hunt.Candidates.Select(c => c.Loss.ToString())));
        Check("The tunnel is where it was", tunnel.LocalPort == port, $"{port} -> {tunnel.LocalPort}");
    }
}
