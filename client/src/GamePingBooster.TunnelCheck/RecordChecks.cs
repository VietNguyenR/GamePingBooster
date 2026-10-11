using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    private const string RegionPlanGolden = "testdata/region-plan-record.json";

    /// <summary>
    /// The region planner's quality record, byte for byte against <see cref="RegionPlanGolden"/> - the file the
    /// licence server's own check parses, so a change to either side's idea of the record fails somewhere.
    /// Set GPB_WRITE_GOLDEN=1 to rewrite the file after a deliberate change, and read the diff.
    /// </summary>
    private static Task ARegionPlanRecordKeepsItsShape()
    {
        // Delta Force from Ha Noi, the case multi-tunnel is for: HCM is best through home (a VN relay), Hong
        // Kong through hk-2, Singapore has no landmark yet.
        var measurements = new List<RegionMeasurement>
        {
            new("hcm", true, 22, new Dictionary<string, double> { ["vn-1"] = 24, ["hk-2"] = 61 }, 31),
            new("hk", true, 58, new Dictionary<string, double> { ["vn-1"] = 57, ["hk-2"] = 37 }, 66),
            new("sg", false, null, new Dictionary<string, double>(), null),
        };
        var plan = RegionPlanner.Plan("vn-2", measurements, new PlannerOptions(false, RegionRouting.MaxTunnels, ["vn-1", "vn-2", "hk-2"]));
        var ways = new Dictionary<string, Dictionary<string, string>>
        {
            ["hcm"] = new() { ["vn-1"] = "vn-1", ["hk-2"] = "hk-2" },
            ["hk"] = new() { ["vn-1"] = "vn-1", ["hk-2"] = "hk-2-hn" },
            ["sg"] = new(),
        };
        var record = new RegionPlanRecord(
            new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero), "after-match", "record", "the game's setting", Acted: false,
            "vn-2", AllowDirect: false, RegionRouting.MaxTunnels, 14.2, Stopped: null, ["vn-1", "hk-2"],
            plan.Select(d =>
            {
                var m = measurements.Single(x => x.RegionId == d.RegionId);
                return new RegionPlanEntry(d.RegionId, m.HasLandmark, m.HomeMs, m.DirectMs, m.ViaRelayMs, ways[d.RegionId],
                    d.Path.ToString(), d.ChosenMs, d.Reason);
            }).ToList());
        var meta = new QualityMeta("0.4.0", "deltaforce", "vn-2", null, "Ho Chi Minh City", "wired");

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            QualityFile.WriteRegionPlanJson(writer, "00000000000000000000000000000000", record, meta);
        }
        var json = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";

        Check("the plan behind the record: hcm stays home, hk leaves for hk-2, sg follows home",
            plan.Select(d => $"{d.RegionId}={d.Path}").SequenceEqual(["hcm=home", "hk=hk-2", "sg=home"]),
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
        Check("no IP address of any kind in the record", !Regex.IsMatch(json, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"));

        var path = Path.Combine(RepositoryRoot(), RegionPlanGolden);
        if (Environment.GetEnvironmentVariable("GPB_WRITE_GOLDEN") == "1")
        {
            File.WriteAllText(path, json);
            Console.WriteLine($"  wrote {RegionPlanGolden}");
        }
        var golden = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
        Check($"the record is exactly {RegionPlanGolden}", golden == json,
            golden is null ? "the file is missing - run with GPB_WRITE_GOLDEN=1" : "it differs - rerun with GPB_WRITE_GOLDEN=1 if the change is meant, and update the licence server's parser");

        // Which tunnel carried a match (5.8) is said only while region routing is in force; a record from one tunnel
        // is unchanged - the golden above has no such field.
        var carried = new MemoryStream();
        using (var writer = new Utf8JsonWriter(carried))
        {
            QualityFile.WriteRegionPlanJson(writer, "00000000000000000000000000000000", record, meta with { Carried = "other" });
        }
        var carriedJson = Encoding.UTF8.GetString(carried.ToArray());
        Check("\"carried\" is written when set, and absent when not",
            carriedJson.Contains("\"carried\":\"other\"") && !json.Contains("carried"), carriedJson);

        // An open tunnel's ways checked by WayCheck (0.3.8): written only when there was one, so the golden above - a plan
        // with no tunnel open - is unchanged, and a moved tunnel says which socket it landed on.
        var withWays = record with
        {
            WayChecks =
            [
                new WayCheckEntry("hk-2", "hk-2", [new WaySample("hk-2", 61.2, new PingLoss(16, 16)), new WaySample("hk-2-hn", 37.4, new PingLoss(16, 15))],
                    "hk-2-hn", "hk-2-hn 37 ms against hk-2 61 ms", Moved: true, OnMeasured: true),
            ],
        };
        var waysBuffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(waysBuffer))
        {
            QualityFile.WriteRegionPlanJson(writer, "00000000000000000000000000000000", withWays, meta);
        }
        var waysJson = Encoding.UTF8.GetString(waysBuffer.ToArray());
        Check("\"wayChecks\" is written when an open tunnel's ways were checked: each way, the choice, the move and the socket",
            !json.Contains("wayChecks") && waysJson.Contains("\"wayChecks\":[{\"relay\":\"hk-2\",\"inUse\":\"hk-2\",\"ways\":[{\"id\":\"hk-2\",\"p50\":61.2,\"sent\":16,\"answered\":16}") &&
            waysJson.Contains("\"moveTo\":\"hk-2-hn\"") && waysJson.Contains("\"moved\":true,\"socket\":\"measured\"") &&
            !Regex.IsMatch(waysJson, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"), waysJson);
        var samplePath = Environment.GetEnvironmentVariable("GPB_WRITE_WAYCHECK_SAMPLE");
        if (!string.IsNullOrEmpty(samplePath)) File.WriteAllText(samplePath, waysJson);
        return Task.CompletedTask;
    }

    private const string LobbyGolden = "testdata/lobby-record.json";

    /// <summary>
    /// The lobby record, byte for byte against <see cref="LobbyGolden"/> - the file the licence server's own check
    /// parses. Twenty seconds - the shortest stretch written - of sg-2 at 58 ms beside vn-5-sg2 at 49, which loses
    /// every tenth probe, and vn-1-sg2, whose first probe was never sent. GPB_WRITE_GOLDEN=1 rewrites it.
    /// </summary>
    private static Task ALobbyRecordKeepsItsShape()
    {
        string[] others = ["vn-5-sg2", "vn-1-sg2"];
        var tracker = new LobbyTracker();
        var start = new DateTimeOffset(2026, 10, 7, 10, 39, 0, TimeSpan.Zero);
        for (var i = 0; i < LobbyTracker.MinTicks; i++)
        {
            tracker.Feed(new QualityTick(i, start.AddMilliseconds(i * SpikeDetector.TickMs))
            {
                Lobby = true,
                RelayProcessSent = true,
                RelayProcessMs = 58 + i % 3 * 0.5,
                CurrentDoor = "sg-2",
                DoorRelayId = "sg-2",
                DoorIds = others,
                DoorSent = [true, i != 0],
                DoorMs = [i % 10 == 0 ? null : 49 + i % 2, i == 0 ? null : 55],
            });
        }
        var lobby = tracker.Feed(new QualityTick(LobbyTracker.MinTicks, start.AddSeconds(20)) { Active = true }).Ended;
        Check("twenty seconds of lobby, then a match: one stretch, ended by the match", lobby is { EndedBy: "match", TickCount: LobbyTracker.MinTicks });
        if (lobby is null) return Task.CompletedTask;

        var meta = new QualityMeta("0.4.4", "pubg", "sg-2", null, "Southeast Asia (Singapore)", "Wi-Fi");
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            QualityFile.WriteLobbyJson(writer, "00000000000000000000000000000000", lobby, meta);
        }
        var json = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
        Check("no IP address of any kind in the record", !Regex.IsMatch(json, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"));
        Check("a probe never sent is null and a lost one -1, as in a spike",
            json.Contains("\"vn-1-sg2\": [\n        null,") && json.Contains("\"vn-5-sg2\": [\n        -1,"), "the series do not start as expected");

        var path = Path.Combine(RepositoryRoot(), LobbyGolden);
        if (Environment.GetEnvironmentVariable("GPB_WRITE_GOLDEN") == "1")
        {
            File.WriteAllText(path, json);
            Console.WriteLine($"  wrote {LobbyGolden}");
        }
        var golden = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
        Check($"the record is exactly {LobbyGolden}", golden == json,
            golden is null ? "the file is missing - run with GPB_WRITE_GOLDEN=1" : "it differs - rerun with GPB_WRITE_GOLDEN=1 if the change is meant, and update the licence server's parser");
        return Task.CompletedTask;
    }

    private const string ProbeRoundGolden = "testdata/probe-round-record.json";

    /// <summary>
    /// The game's own probes before a match, through ProbeTap and into the record, byte for byte against
    /// <see cref="ProbeRoundGolden"/> - the file the licence server's own check parses. PUBG's two Singapore
    /// endpoints and one Korean, probed once a second for five seconds; one Singapore probe never answered; an
    /// endpoint the profile does not know probed on the port the landmarks taught; a match server's packets on
    /// another port ignored. GPB_WRITE_GOLDEN=1 rewrites it.
    /// </summary>
    private static Task AProbeRoundRecordKeepsItsShape()
    {
        var tap = new GamePingBooster.Service.Discovery.ProbeTap(
        [
            ("asia-kr", ["20.196.246.162"]),
            ("asia-sg", ["20.43.187.66", "20.43.187.70"]),
        ]);
        long Ticks(double ms) => (long)(ms / 1000 * System.Diagnostics.Stopwatch.Frequency);
        const long t0 = 1_000_000_000;
        var sg1 = System.Net.IPAddress.Parse("20.43.187.66");
        var sg2 = System.Net.IPAddress.Parse("20.43.187.70");
        var kr = System.Net.IPAddress.Parse("20.196.246.162");
        var unknown = System.Net.IPAddress.Parse("20.44.1.1");
        var server = System.Net.IPAddress.Parse("20.196.10.10");

        // Before any landmark: port 8081 is not learned yet, so this one is not a probe.
        tap.ObserveForTest(true, unknown, 8081, t0 - Ticks(500));
        for (var i = 0; i < 5; i++)
        {
            var at = t0 + Ticks(i * 1000);
            tap.ObserveForTest(true, sg1, 8081, at);
            if (i != 2) tap.ObserveForTest(false, sg1, 8081, at + Ticks(55 + i));
            tap.ObserveForTest(true, sg2, 8081, at + Ticks(5));
            tap.ObserveForTest(false, sg2, 8081, at + Ticks(5 + 58));
            tap.ObserveForTest(true, kr, 8081, at + Ticks(10));
            tap.ObserveForTest(false, kr, 8081, at + Ticks(10 + 100 + i));
            tap.ObserveForTest(true, server, 27015, at + Ticks(20));
        }
        tap.ObserveForTest(true, unknown, 8081, t0 + Ticks(6000));
        tap.ObserveForTest(false, unknown, 8081, t0 + Ticks(6070));
        // Two sends 10 ms apart and one reply: paired with the later send, so 60 and not 70.
        tap.ObserveForTest(true, unknown, 8081, t0 + Ticks(7000));
        tap.ObserveForTest(true, unknown, 8081, t0 + Ticks(7010));
        tap.ObserveForTest(false, unknown, 8081, t0 + Ticks(7070));

        var matchAt = t0 + Ticks(30_000);
        var round = tap.Take(matchAt, receivesVerified: true,
            (t0 + Ticks(40_000), new DateTimeOffset(2026, 10, 11, 8, 0, 40, TimeSpan.Zero)));
        Check("a round is made from the landmarks' probes", round is not null);
        if (round is null) return Task.CompletedTask;
        Check("the match is dated from the clock given", round.MatchUtc == new DateTimeOffset(2026, 10, 11, 8, 0, 30, TimeSpan.Zero), round.MatchUtc.ToString("o"));
        Check("landmarks first, in the order first probed, then the unmapped endpoint; the match server is not a probe",
            string.Join(",", round.Endpoints.Select(e => $"{e.Region ?? "?"}#{e.Index}")) == "asia-sg#0,asia-sg#1,asia-kr#0,?#0",
            string.Join(",", round.Endpoints.Select(e => $"{e.Region ?? "?"}#{e.Index}")));
        var first = round.Endpoints[0];
        Check("a probe never answered is lost, and the rest are paired with their own send",
            first is { Sent: 5, Received: 4, Answered: 4, MinMs: >= 54.9 and <= 55.1, MaxMs: >= 58.9 and <= 59.1 }, $"{first}");
        var unmapped = round.Endpoints[3];
        Check("an unmapped endpoint counts only after the port is learned, and a reply pairs with the latest send",
            unmapped is { Sent: 3, Answered: 2, MinMs: >= 59.9 and <= 60.1 }, $"{unmapped}");
        Check("the tap is empty after a round is taken", tap.Take(matchAt, true) is null);

        var meta = new QualityMeta("0.4.7", "pubg", "sg-3", "vn-5-sg3", "Korea (Seoul)", "wired");
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            QualityFile.WriteProbeRoundJson(writer, "00000000000000000000000000000000", round, "off-tunnel", meta);
        }
        var json = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
        Check("no IP address of any kind in the record", !Regex.IsMatch(json, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"));

        var path = Path.Combine(RepositoryRoot(), ProbeRoundGolden);
        if (Environment.GetEnvironmentVariable("GPB_WRITE_GOLDEN") == "1")
        {
            File.WriteAllText(path, json);
            Console.WriteLine($"  wrote {ProbeRoundGolden}");
        }
        var golden = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
        Check($"the record is exactly {ProbeRoundGolden}", golden == json,
            golden is null ? "the file is missing - run with GPB_WRITE_GOLDEN=1" : "it differs - rerun with GPB_WRITE_GOLDEN=1 if the change is meant, and update the licence server's parser");
        return Task.CompletedTask;
    }

    /// <summary>The directory holding testdata/, found upwards from the build output.</summary>
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "testdata")) && Directory.Exists(Path.Combine(dir.FullName, "client"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("No testdata/ above the build output.");
    }
}
