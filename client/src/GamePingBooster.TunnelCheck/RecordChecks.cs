using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GamePingBooster.Core.Paths;
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
            new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero), "record", "the game's setting", Acted: false,
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
