using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GamePingBooster.Core.Paths;

/// <summary>
/// Which region of the running game a destination address belongs to - the question the uplink asks of
/// every packet once the regions can leave by different tunnels. See docs/MULTI-TUNNEL.md, section 5.1.
///
/// Built once per game from its regions' ranges and read-only after, so the packet path reads it without a
/// lock. The ranges are flattened into sorted, non-overlapping intervals and searched by bisection: a game
/// has tens to a few hundred ranges, and one lookup is a handful of comparisons.
///
/// REFUSES A GAME WHOSE REGIONS OVERLAP. A /20 in one region holding a /32 in another has no single answer:
/// Windows would route by the longest prefix, and a table that answered differently would send the packet
/// through a tunnel the planner did not choose for it. The game then runs on one tunnel, as it does today,
/// and <see cref="Problems"/> says which ranges collide. Ranges overlapping inside ONE region are merged -
/// that is only a range written twice.
///
/// A range that is not an IPv4 CIDR is skipped and named, the way the route installer skips it.
/// </summary>
public sealed class RegionTable
{
    private readonly uint[] _starts;
    private readonly uint[] _ends;
    private readonly int[] _regions;
    private readonly string[] _regionIds;

    /// <summary>Why the table could not be used, or what it skipped. Empty for a clean game.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>False when two regions overlap; the game must then stay on one tunnel.</summary>
    public bool Usable { get; }

    public int RegionCount => _regionIds.Length;

    public string RegionIdAt(int index) => _regionIds[index];

    private RegionTable(uint[] starts, uint[] ends, int[] regions, string[] regionIds, List<string> problems, bool usable)
    {
        _starts = starts;
        _ends = ends;
        _regions = regions;
        _regionIds = regionIds;
        Problems = problems;
        Usable = usable;
    }

    /// <summary>The table for these regions, in the order given - the index <see cref="Find"/> returns is the position here.</summary>
    public static RegionTable Build(IReadOnlyList<(string RegionId, IReadOnlyList<string> Cidrs)> regions)
    {
        var problems = new List<string>();
        var intervals = new List<(uint Start, uint End, int Region, string Cidr)>();

        for (var r = 0; r < regions.Count; r++)
        {
            foreach (var cidr in regions[r].Cidrs)
            {
                if (!TryParseCidr(cidr, out var start, out var end))
                {
                    problems.Add($"'{cidr}' in region '{regions[r].RegionId}' is not an IPv4 range - skipped.");
                    continue;
                }
                intervals.Add((start, end, r, cidr));
            }
        }

        intervals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        var starts = new List<uint>();
        var ends = new List<uint>();
        var owners = new List<int>();
        var cidrs = new List<string>();
        var usable = true;

        foreach (var (start, end, region, cidr) in intervals)
        {
            var last = starts.Count - 1;
            if (last >= 0 && start <= ends[last])
            {
                if (owners[last] != region)
                {
                    usable = false;
                    problems.Add($"'{cidr}' in region '{regions[region].RegionId}' overlaps '{cidrs[last]}' in region " +
                                 $"'{regions[owners[last]].RegionId}' - one address cannot belong to two regions.");
                    continue;
                }
                if (end > ends[last]) ends[last] = end;
                continue;
            }
            starts.Add(start);
            ends.Add(end);
            owners.Add(region);
            cidrs.Add(cidr);
        }

        return new RegionTable([.. starts], [.. ends], [.. owners], [.. regions.Select(r => r.RegionId)], problems, usable);
    }

    /// <summary>The index of the region holding <paramref name="address"/> (host order), or -1 when none does.</summary>
    public int Find(uint address)
    {
        int lo = 0, hi = _starts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (address < _starts[mid]) hi = mid - 1;
            else if (address > _ends[mid]) lo = mid + 1;
            else return _regions[mid];
        }
        return -1;
    }

    /// <summary>An IPv4 address as the host-order number <see cref="Find"/> takes.</summary>
    public static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (address.AddressFamily != AddressFamily.InterNetwork || !address.TryWriteBytes(bytes, out _))
        {
            throw new ArgumentException("Not an IPv4 address.", nameof(address));
        }
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>
    /// "a.b.c.d/n" as the first and last address it covers. Host bits are ignored, as Windows ignores them
    /// when the route is added.
    /// </summary>
    public static bool TryParseCidr(string? cidr, out uint start, out uint end)
    {
        start = end = 0;
        if (cidr is null) return false;
        var slash = cidr.IndexOf('/');
        if (slash <= 0) return false;
        if (!IPAddress.TryParse(cidr.AsSpan(0, slash), out var network) ||
            network.AddressFamily != AddressFamily.InterNetwork) return false;
        if (!int.TryParse(cidr.AsSpan(slash + 1), out var bits) || bits is < 1 or > 32) return false;

        var mask = uint.MaxValue << (32 - bits);
        start = ToUInt32(network) & mask;
        end = start | ~mask;
        return true;
    }
}
