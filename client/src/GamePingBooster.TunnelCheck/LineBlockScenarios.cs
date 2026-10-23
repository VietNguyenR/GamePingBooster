using GamePingBooster.Service.Dns;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// One LineBlockWatch run on this machine's line, printed - what the PC tune-up panel's "Nhà mạng" line would say.
///
///     gpb-tunnelcheck lineblocks                       Steam (built in) + PUBG's lobby names
///     gpb-tunnelcheck lineblocks PUBG=a.com,b.com ...  your own services: canary first, then other names
///
/// Run with nothing connected and no VPN or WARP up, or it measures that instead of the line.
/// </summary>
internal static partial class Program
{
    private static int LineBlocksMain(string[] args)
    {
        var apps = new List<UnblockApp>();
        foreach (var arg in args)
        {
            var (name, list) = arg.Split('=', 2) is [var n, var l] ? (n, l) : throw new ArgumentException($"'{arg}' is not Name=canary,other");
            var names = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            apps.Add(new UnblockApp(name.ToLowerInvariant(), name, [.. names], [], names[0], Tunnel: [.. names.Skip(1)]));
        }

        if (apps.Count == 0)
        {
            apps.AddRange(UnblockPolicy.Builtin.Apps);
            apps.Add(new UnblockApp("pubg", "PUBG", ["playbattlegrounds.com"], [], "prod-live-front.playbattlegrounds.com",
                Tunnel: ["prod-live-cfentry.playbattlegrounds.com", "prod-live-images.playbattlegrounds.com"]));
        }

        var policy = new UnblockPolicy(apps, "tunnelcheck");
        var ip = GamePingBooster.Service.Dns.PublicAddress.AskAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"Measuring {policy.Describe()} on this line (public address {ip?.ToString() ?? "unknown"})...");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = LineBlockWatch.MeasureAsync(policy, CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine($"  took {clock.Elapsed.TotalSeconds:0.0} s, measured: {result.Measured}");
        Console.WriteLine(result.Blocked.Count == 0 ? "  nothing broken by the line" : "  broken: " + string.Join(", ", result.Blocked));
        foreach (var finding in result.Findings) Console.WriteLine("    " + finding);
        return 0;
    }
}
