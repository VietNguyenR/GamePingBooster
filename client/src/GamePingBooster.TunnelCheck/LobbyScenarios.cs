using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The lobby record (2026-10-07): the game open with no match, the recorder comparing the ways in, and a move made in
/// the lobby. Real tunnel, real recorder, real Probes; the supervisor's part is the callback, as in
/// ARegionsTunnelMovesItsWayInLikeHome. Twenty-two calm seconds, then the direct road 40 ms slow until the policy
/// leaves it, twenty-one seconds on the entry, then a match. About a minute. The records go to a scratch folder, never
/// this PC's quality folder or upload queue.
///
/// The steady rule itself is QualityCheck's: Task.Delay on Windows rounds to the 15.6 ms timer, so a fake relay
/// cannot hold a gap under the 10 ms "worse" bar, and this scenario uses the "worse" one to move.
/// </summary>
internal static partial class Program
{
    private static async Task TheLobbyIsRecordedAndAMoveInItEndsTheRecord()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"gpb-tunnelcheck-lobby-{Environment.ProcessId}");
        QualityFile.DirectoryOverride = scratch;
        try
        {
            await LobbyRecords(scratch);
        }
        finally
        {
            QualityFile.DirectoryOverride = null;
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task LobbyRecords(string scratch)
    {
        using var relay = new FakeRelay(Psk, doors: 2, firstInner: 2);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, 61);
        var adapterIp = tunnel.InnerIp;

        var gate = new object();
        string? entry = null;
        IReadOnlyList<DoorProbes.Door> doors = [new("vn-2-b", relay.Door(1))];
        var decisions = new ConcurrentQueue<DoorDecision>();

        SpikeRecorder.Context Context()
        {
            lock (gate)
            {
                return new SpikeRecorder.Context(tunnel, "vn-2", entry, "VN 2", null, null, null, "tunnelcheck", GameRunning: true,
                    doors, MovesEnabled: true);
            }
        }

        bool Move(DoorDecision decision)
        {
            decisions.Enqueue(decision);
            if (decision.To != "vn-2-b") return false;
            lock (gate)
            {
                tunnel.MoveTo(relay.Door(1));
                entry = "vn-2-b";
                doors = [new("vn-2", relay.Door(0))];
            }
            return true;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(rig.Cts.Token);
        var recorder = new SpikeRecorder(Context, line => Log.Enqueue(line), Move);
        var running = Task.Run(() => recorder.RunAsync(stop.Token));

        List<JsonElement> Lobbies() => Directory.Exists(scratch)
            ? Directory.GetFiles(scratch, "quality-*.jsonl")
                .SelectMany(File.ReadLines)
                .Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(r => r.GetProperty("type").GetString() == "lobby")
                .ToList()
            : [];

        // The game open, no match: nothing but the recorder's own pongs and Probes cross the tunnel.
        await Task.Delay(22_000);
        Check("22 s in the lobby with every way alike: nothing moved, nothing written yet",
            decisions.IsEmpty && Lobbies().Count == 0, $"{decisions.Count} decision(s), {Lobbies().Count} record(s)");

        relay.DoorDelayMs[0] = 40;
        var moved = await WaitUntil(() => decisions.Any(d => d.To == "vn-2-b"), 20_000);
        Check("The direct road 40 ms slow: the policy leaves it for vn-2-b in the lobby", moved,
            string.Join("; ", decisions.Select(d => $"{d.From} -> {d.To}")));
        var written = await WaitUntil(() => Lobbies().Count == 1, 5_000);
        var first = Lobbies().FirstOrDefault();
        Check("  ...and the lobby on vn-2 is written as it ends: by the move, from vn-2",
            written && first.GetProperty("endedBy").GetString() == "moved" && first.GetProperty("door").GetString() == "vn-2" &&
            first.GetProperty("seconds").GetDouble() >= 25,
            written ? first.ToString() : "no record");
        if (written)
        {
            var ways = first.GetProperty("ways").EnumerateObject().Select(p => p.Name).ToList();
            var ticks = first.GetProperty("ticks");
            var pongs = ticks.GetProperty("relayProcess").GetArrayLength();
            var probes = ticks.GetProperty("doors").GetProperty("vn-2-b").GetArrayLength();
            var answered = first.GetProperty("ways").GetProperty("vn-2-b").GetProperty("sent").GetInt32() -
                           first.GetProperty("ways").GetProperty("vn-2-b").GetProperty("lost").GetInt32();
            Check("  ...with both ways' figures, the way in use first, and every quarter second of both",
                ways.SequenceEqual(["vn-2", "vn-2-b"]) && pongs == probes && pongs >= 100 && answered >= pongs * 9 / 10,
                $"ways {string.Join(",", ways)}, {pongs} pongs, {probes} probes, {answered} answered");
            Check("  ...and no IP address of any kind", !Regex.IsMatch(first.ToString(), @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"));
        }

        // On the entry now: another lobby stretch, then the game starts sending - a match.
        await Task.Delay(21_000);
        var sent = 0;
        var playing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                rig.Device.FromWindows(GameChecked(adapterIp, SgServer, sent++));
                await Task.Delay(25);
            }
        });
        var second = await WaitUntil(() => Lobbies().Count == 2, 8_000);
        var last = Lobbies().LastOrDefault();
        Check("The lobby on vn-2-b is written when the match starts",
            second && last.GetProperty("endedBy").GetString() == "match" && last.GetProperty("door").GetString() == "vn-2-b" &&
            last.GetProperty("entry").GetString() == "vn-2-b",
            second ? last.ToString() : $"{Lobbies().Count} record(s)");

        stop.Cancel();
        await Task.WhenAll(running, playing);
        Check("One move, no flapping", decisions.Count == 1, string.Join("; ", decisions.Select(d => $"{d.From} -> {d.To}")));
    }
}
