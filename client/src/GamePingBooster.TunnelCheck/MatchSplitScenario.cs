using System.Text.Json;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// A match that moves to another relay or region part-way is two records, each labelled with what carried it. One
/// record used to take the labels of its LAST tick: on 2026-10-01 a Singapore match carried by sg-2 ended just as the
/// carrier went back to home, and the record read "vn-1, Ho Chi Minh City" over sg-2's numbers - which then passed for
/// a slow Ho Chi Minh City match. About 70 s: two halves past the recorder's thirty-second minimum. The summaries go to
/// a scratch folder, never this PC's quality folder or upload queue.
/// </summary>
internal static partial class Program
{
    private static async Task AMatchThatMovesRelayIsTwoRecords()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"gpb-tunnelcheck-split-{Environment.ProcessId}");
        QualityFile.DirectoryOverride = scratch;
        try
        {
            await MatchSplits(scratch);
        }
        finally
        {
            QualityFile.DirectoryOverride = null;
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task MatchSplits(string scratch)
    {
        using var m = new MultiRig();
        await m.StartAsync(clientId: 47);
        m.KrViaOther();

        var onOther = false;
        SpikeRecorder.Context Context() => onOther
            ? new SpikeRecorder.Context(m.Other, "hk-2", null, "HK 2", null, null, "Singapore", "tunnelcheck",
                GameRunning: true, [], MovesEnabled: false, Carried: "other")
            : new SpikeRecorder.Context(m.Home, "sg-2", null, "SG 2", null, null, "Ho Chi Minh City", "tunnelcheck",
                GameRunning: true, [], MovesEnabled: false, Carried: "home");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(m.Rig.Cts.Token);
        var recorder = new SpikeRecorder(Context, line => Log.Enqueue(line));
        var running = Task.Run(() => recorder.RunAsync(stop.Token));

        // 40 packets a second all the way through, to home's server for 32 s and then to the other relay's - the
        // game's stream never breaks, so nothing but the move can end the first record.
        async Task Play(uint server, TimeSpan forHowLong)
        {
            var until = Environment.TickCount64 + (long)forHowLong.TotalMilliseconds;
            var sequence = 0;
            while (!stop.IsCancellationRequested && Environment.TickCount64 < until)
            {
                m.Rig.Device.FromWindows(GameChecked(m.AdapterIp, server, sequence++));
                await Task.Delay(25);
            }
        }

        await Play(SgServer, TimeSpan.FromSeconds(32));
        onOther = true;
        await Play(KrServer, TimeSpan.FromSeconds(33));
        stop.Cancel();
        await running;

        var matches = Directory.Exists(scratch)
            ? Directory.GetFiles(scratch, "quality-*.jsonl")
                .SelectMany(File.ReadLines)
                .Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(r => r.GetProperty("type").GetString() == "match")
                .ToList()
            : [];
        string? Field(JsonElement r, string name) => r.TryGetProperty(name, out var v) ? v.GetString() : null;

        Check("The match is written as two records, not one", matches.Count == 2, $"{matches.Count} record(s)");
        if (matches.Count != 2) return;
        Check("The first is home's: sg-2, Ho Chi Minh City, carried by home",
            Field(matches[0], "relay") == "sg-2" && Field(matches[0], "region") == "Ho Chi Minh City" &&
            Field(matches[0], "carried") == "home",
            $"{Field(matches[0], "relay")}, {Field(matches[0], "region")}, {Field(matches[0], "carried")}");
        Check("The second is the other relay's: hk-2, Singapore, carried by the other",
            Field(matches[1], "relay") == "hk-2" && Field(matches[1], "region") == "Singapore" &&
            Field(matches[1], "carried") == "other",
            $"{Field(matches[1], "relay")}, {Field(matches[1], "region")}, {Field(matches[1], "carried")}");
    }
}
