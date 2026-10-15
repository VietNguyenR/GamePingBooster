using System.Net;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// How a game probes its datacentres, declared rather than learned: the remote port its probes use, and the Azure
/// regions it probes, each under the region name the round records it by.
///
/// Why it is declared. Until 2026-10-14 PUBG probed eight fixed UDP 8081 endpoints, the profile's landmarks, and
/// <see cref="ProbeTap"/> learned the port from them. Its patch that day moved the probes onto addresses that rotate
/// every matchmaking session inside the same five Azure regions (owner capture 2026-10-15), so no landmark was ever
/// probed again and no round was made. The port and the regions held; the addresses did not. So the port is declared
/// here, and an endpoint's region is read from the region's published prefixes (<see cref="ProbeRegionTable"/>).
///
/// Region names. A region the profile also has keeps the profile's id (asia-sg, asia-kr), so rounds from before
/// and after the patch read alike on the licence server. The other three are named here; the profile never holds
/// them, because a range in the profile is routed and a probe must never be (docs: probes-use-real-isp-path).
/// Nothing here is ever routed: the table only names.
/// </summary>
internal sealed class ProbePlan
{
    /// <summary>PUBG: UDP 8081 to one Azure region each, from its 2026-10-15 capture.</summary>
    internal static readonly ProbePlan Pubg = new(8081,
    [
        ("asia-sg", "southeastasia"),
        ("asia-kr", "koreacentral"),
        ("eu-de", "germanywc"),
        ("us-central", "centralus"),
        ("sa-br", "brazilsouth"),
    ]);

    /// <summary>The declared plan for a game by its profile id, or null for a game that declares none.</summary>
    public static ProbePlan? For(string gameId) => gameId switch
    {
        "pubg" => Pubg,
        _ => null,
    };

    public ushort Port { get; }

    /// <summary>Address ranges sorted by start, each with the region it names. Built once per plan.</summary>
    private readonly (uint Start, uint End, string Region)[] _ranges;

    internal ProbePlan(ushort port, IReadOnlyList<(string Region, string AzureRegion)> regions)
    {
        Port = port;
        var ranges = new List<(uint, uint, string)>();
        foreach (var (region, azure) in regions)
        {
            var prefixes = ProbeRegionTable.Regions.FirstOrDefault(r => r.AzureRegion == azure).Prefixes
                ?? throw new InvalidOperationException($"ProbeRegionTable has no {azure} - rerun Build-ProbeRegions.ps1.");
            foreach (var cidr in prefixes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Range(cidr) is { } range) ranges.Add((range.Start, range.End, region));
            }
        }
        _ranges = Disjoint(ranges);
    }

    /// <summary>The region <paramref name="address"/> lies in, or null when no declared region holds it.</summary>
    public string? RegionOf(uint address) => Find(_ranges, address);

    /// <summary>
    /// <paramref name="ranges"/> sorted by start with every overlap folded away, so one range at most can hold an
    /// address. A prefix nested in another (Azure publishes both) is merged into it; where two regions overlap -
    /// which Azure's do not - the range met first keeps the addresses.
    /// </summary>
    internal static (uint Start, uint End, string Region)[] Disjoint(IEnumerable<(uint Start, uint End, string Region)> ranges)
    {
        var merged = new List<(uint Start, uint End, string Region)>();
        foreach (var range in ranges.OrderBy(r => r.Start).ThenByDescending(r => r.End))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var last = merged[^1];
                if (range.End <= last.End) continue;
                if (range.Region == last.Region) { merged[^1] = last with { End = range.End }; continue; }
                merged.Add((last.End + 1, range.End, range.Region));
                continue;
            }
            merged.Add(range);
        }
        return [.. merged];
    }

    /// <summary>The range in <paramref name="disjoint"/> (from <see cref="Disjoint"/>) holding <paramref name="address"/>.</summary>
    internal static string? Find((uint Start, uint End, string Region)[] disjoint, uint address)
    {
        int lo = 0, hi = disjoint.Length - 1, last = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (disjoint[mid].Start <= address) { last = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return last >= 0 && address <= disjoint[last].End ? disjoint[last].Region : null;
    }

    /// <summary>"a.b.c.d/n" as its first and last address, or null when it is not an IPv4 prefix.</summary>
    internal static (uint Start, uint End)? Range(string cidr)
    {
        if (!IPNetwork.TryParse(cidr, out var network) ||
            network.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var start = ProbeTap.ToUInt(network.BaseAddress);
        var size = network.PrefixLength == 0 ? uint.MaxValue : (1u << (32 - network.PrefixLength)) - 1;
        return (start, start + size);
    }
}
