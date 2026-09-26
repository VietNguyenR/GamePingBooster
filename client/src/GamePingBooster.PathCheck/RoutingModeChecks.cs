using System.Text.Json;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    /// <summary>Where a connection's region-routing mode comes from. docs/MULTI-TUNNEL.md, section 8.1.</summary>
    private static void RoutingModeChecks()
    {
        static RegionRoutingMode Mode(string? local, string? game, bool routed = false) => RegionRouting.Resolve(local, game, routed).Mode;

        Check("neither config.json nor the game says: off", Mode(null, null) == RegionRoutingMode.Off);
        Check("the game's setting is used", Mode(null, "record") == RegionRoutingMode.Record && Mode(null, "on") == RegionRoutingMode.On);
        Check("config.json wins over the game", Mode("off", "on") == RegionRoutingMode.Off && Mode("on", "off") == RegionRoutingMode.On);
        Check("a blank config.json value does not decide", Mode("  ", "on") == RegionRoutingMode.On);
        Check("a typo in config.json is record, not the game's value", Mode("onn", "on") == RegionRoutingMode.Record);
        Check("an unknown game value is off", Mode(null, "sometimes") == RegionRoutingMode.Off);
        Check("case and spaces do not matter", Mode(" RECORD ", null) == RegionRoutingMode.Record);
        Check("routed landmarks are off whatever config.json says", Mode("on", "on", routed: true) == RegionRoutingMode.Off);
        Check("and the log says why", RegionRouting.Resolve("on", "on", true).Source.Contains("routed"));
        Check("three tunnels at most, home included", RegionRouting.MaxTunnels == 3);

        // The fields as the licence server sends them, through the same source-generated reader the service uses.
        var bundle = JsonSerializer.Deserialize(
            """{"games":[{"id":"deltaforce","name":"Delta Force","processNames":["x.exe"],"regions":[],"regionRouting":"record","regionDirect":true},{"id":"old","name":"Old","processNames":["y.exe"],"regions":[]}],"relays":[]}""",
            ProfileJsonContext.Default.ProfileBundle)!;
        Check("regionRouting is read from the profile", bundle.Games[0].RegionRouting == "record");
        Check("regionDirect is read from the profile", bundle.Games[0].RegionDirect);
        Check("a profile without them reads as off and no direct",
            bundle.Games[1].RegionRouting is null && !bundle.Games[1].RegionDirect &&
            Mode(null, bundle.Games[1].RegionRouting) == RegionRoutingMode.Off);
    }
}
