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
    /// <see cref="ProbeRoundGolden"/> - the file the licence server's own check parses. The endpoints are the ones
    /// PUBG probed in the owner's capture of 2026-10-15, after its 10-14 patch: none of them a landmark, rotating per
    /// session, three or four packets each, named by ProbePlan.Pubg's Azure regions (the round trips are made up).
    /// One Singapore probe never answered; an endpoint on port 8081 outside every declared region kept as unmapped,
    /// with two sends 10 ms apart and one reply; a match server's packets on another port ignored. Then, apart from
    /// the golden: a landmark still names itself, the profile's ranges name what the plan does not, and a game
    /// without a plan still learns the port from a landmark. GPB_WRITE_GOLDEN=1 rewrites it.
    /// </summary>
    private static Task AProbeRoundRecordKeepsItsShape()
    {
        // The profile's own landmark lists (pubg-vn.json): the capture's endpoints are indexed after them.
        IReadOnlyList<(string, IReadOnlyList<string>, IReadOnlyList<string>)> profile =
        [
            ("asia-sg", ["20.43.187.66", "20.43.187.70"], ["57.155.0.0/22"]),
            ("asia-jp", [], []),
            ("asia-kr", ["20.196.246.162", "20.214.91.184", "20.196.246.195"], []),
        ];
        var tap = new GamePingBooster.Service.Discovery.ProbeTap(profile, GamePingBooster.Service.Discovery.ProbePlan.Pubg);
        long Ticks(double ms) => (long)(ms / 1000 * System.Diagnostics.Stopwatch.Frequency);
        const long t0 = 1_000_000_000;
        static System.Net.IPAddress Ip(string s) => System.Net.IPAddress.Parse(s);

        // (endpoint, packets sent, round trip ms) in the order the game first probed them.
        (string Address, int Sends, double Ms)[] probes =
        [
            ("57.155.2.179", 4, 38),
            ("20.188.112.149", 4, 41),
            ("20.41.114.247", 4, 97),
            ("9.141.72.225", 3, 251),
            ("20.52.0.2", 3, 255),
            ("20.46.244.68", 3, 221),
            ("20.118.232.98", 3, 226),
            ("74.163.89.150", 3, 331),
            ("191.233.3.60", 3, 336),
        ];
        var server = Ip("20.6.0.10");
        var outside = Ip("203.0.113.9");
        for (var i = 0; i < 4; i++)
        {
            for (var p = 0; p < probes.Length; p++)
            {
                if (i >= probes[p].Sends) continue;
                var at = t0 + Ticks(i * 1000 + p * 20);
                var address = Ip(probes[p].Address);
                tap.ObserveForTest(true, address, 8081, at);
                // 20.188.112.149's third probe is never answered.
                if (!(p == 1 && i == 2)) tap.ObserveForTest(false, address, 8081, at + Ticks(probes[p].Ms + i));
            }
            tap.ObserveForTest(true, server, 7777, t0 + Ticks(i * 1000 + 500));
            tap.ObserveForTest(false, server, 7777, t0 + Ticks(i * 1000 + 540));
        }
        // Two sends 10 ms apart and one reply: paired with the later send, so 60 and not 70.
        tap.ObserveForTest(true, outside, 8081, t0 + Ticks(5000));
        tap.ObserveForTest(true, outside, 8081, t0 + Ticks(5010));
        tap.ObserveForTest(false, outside, 8081, t0 + Ticks(5070));

        var matchAt = t0 + Ticks(30_000);
        var round = tap.Take(matchAt, receivesVerified: true,
            (t0 + Ticks(40_000), new DateTimeOffset(2026, 10, 15, 8, 0, 40, TimeSpan.Zero)));
        Check("a round is made from probes on the declared port, though none reached a landmark", round is not null);
        if (round is null) return Task.CompletedTask;
        Check("the match is dated from the clock given", round.MatchUtc == new DateTimeOffset(2026, 10, 15, 8, 0, 30, TimeSpan.Zero), round.MatchUtc.ToString("o"));
        var names = string.Join(",", round.Endpoints.Select(e => $"{e.Region ?? "?"}#{e.Index}"));
        Check("every capture endpoint named by its Azure region, indexed after the region's landmarks, in the order " +
              "first probed, then the unmapped one; the match server is not a probe",
            names == "asia-sg#2,asia-sg#3,asia-kr#3,eu-de#0,eu-de#1,us-central#0,us-central#1,sa-br#0,sa-br#1,?#0", names);
        var lossy = round.Endpoints[1];
        Check("a probe never answered is lost, and the rest are paired with their own send",
            lossy is { Sent: 4, Received: 3, Answered: 3, MinMs: >= 40.9 and <= 41.1, MaxMs: >= 43.9 and <= 44.1 }, $"{lossy}");
        var unmapped = round.Endpoints[^1];
        Check("an endpoint on the probe port outside every region is kept, and a reply pairs with the latest send",
            unmapped is { Region: null, Sent: 2, Answered: 1, MinMs: >= 59.9 and <= 60.1 }, $"{unmapped}");
        Check("the tap is empty after a round is taken", tap.Take(matchAt, true) is null);

        // Apart from the golden: an old landmark keeps its own index; an address no Azure region of the plan holds
        // is named by the profile's ranges; and on the probe port with no known region at all, no round is made.
        var other = new GamePingBooster.Service.Discovery.ProbeTap(
            [("asia-sg", ["20.43.187.66", "20.43.187.70"], []), ("asia-jp", [], ["198.51.100.0/24"])],
            GamePingBooster.Service.Discovery.ProbePlan.Pubg);
        other.ObserveForTest(true, Ip("20.43.187.70"), 8081, t0);
        other.ObserveForTest(true, Ip("198.51.100.7"), 8081, t0 + Ticks(10));
        var named = other.Take(matchAt, true)?.Endpoints.Select(e => $"{e.Region}#{e.Index}");
        Check("a landmark names its own index, and the profile's ranges name what the plan does not",
            named is not null && string.Join(",", named) == "asia-sg#1,asia-jp#0", named is null ? "no round" : string.Join(",", named));
        other.ObserveForTest(true, outside, 8081, t0);
        Check("probes to no known region alone are not a round", other.Take(matchAt, true) is null);

        // A game with no plan: the port is still learned from a landmark, as before the patch.
        var learnt = new GamePingBooster.Service.Discovery.ProbeTap([("asia-sg", ["20.43.187.66"], [])]);
        learnt.ObserveForTest(true, outside, 9000, t0);
        learnt.ObserveForTest(true, Ip("20.43.187.66"), 9000, t0 + Ticks(10));
        learnt.ObserveForTest(true, outside, 9000, t0 + Ticks(20));
        var learned = learnt.Take(matchAt, true);
        Check("without a plan the port is learned from a landmark, and only later packets on it count",
            learned is { Endpoints: [{ Region: "asia-sg", Index: 0 }, { Region: null, Sent: 1 }] },
            learned is null ? "no round" : string.Join(",", learned.Endpoints.Select(e => $"{e.Region ?? "?"}#{e.Index} {e.Sent}")));
        Check("no plan and no landmark: the tap can never match",
            !new GamePingBooster.Service.Discovery.ProbeTap([("asia-sg", [], ["57.155.0.0/22"])]).CanMatch);

        var meta = new QualityMeta("0.5.1", "pubg", "sg-3", "vn-6-sg3", "Southeast Asia (Singapore)", "wired");
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
