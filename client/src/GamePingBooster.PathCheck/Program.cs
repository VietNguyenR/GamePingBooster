namespace GamePingBooster.PathCheck;

/// <summary>
/// Checks the pieces multi-tunnel stands on, before any of them carries a packet: the inner-address NAT,
/// the region table, the sticky destinations and the planner. See docs/MULTI-TUNNEL.md, section 10.1.
///
///     dotnet run --project client/src/GamePingBooster.PathCheck
///
/// A plain console program, like ProtocolCheck and QualityCheck: this repository carries no test framework.
///
/// Two kinds of check. Named cases, one rule each, for the shapes that have a reason to exist - an ICMP
/// error quoting our packet, a first fragment, a UDP checksum of zero. And properties over tens of
/// thousands of random inputs from a fixed seed, for the claims that have to hold for EVERY input: every
/// rewritten checksum verifies by full recomputation with an implementation that shares no code with the
/// one under test; a rewrite undone gives back the original byte for byte; no plan ever gives a region a
/// path slower than home. A failure prints the seed and the first input that broke it.
/// </summary>
internal static partial class Program
{
    private static int _failures;

    /// <summary>Fixed, so a failure is reproducible. Printed with every property failure.</summary>
    internal const int Seed = 20260925;

    private static int Main()
    {
        Console.WriteLine("Inner-address NAT:");
        NatChecks();

        Console.WriteLine();
        Console.WriteLine("Region table:");
        TableChecks();

        Console.WriteLine();
        Console.WriteLine("Sticky destinations:");
        StickyChecks();

        Console.WriteLine();
        Console.WriteLine("Region planner:");
        PlannerChecks();

        Console.WriteLine();
        Console.WriteLine("Region routing mode:");
        RoutingModeChecks();

        Console.WriteLine();
        Console.WriteLine("Which tunnel carries the match:");
        CarrierChecks();

        Console.WriteLine();
        Console.WriteLine("When the lobby is sure enough to plan:");
        LobbyChecks();

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All path checks passed.");
            return 0;
        }
        Console.WriteLine($"{_failures} path check(s) FAILED.");
        return 1;
    }

    internal static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            Console.WriteLine($"  ok    {name}");
            return;
        }
        _failures++;
        Console.WriteLine($"  FAIL  {name}: {detail}");
    }
}
