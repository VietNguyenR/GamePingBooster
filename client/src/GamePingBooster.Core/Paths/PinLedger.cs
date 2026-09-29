namespace GamePingBooster.Core.Paths;

/// <summary>Who wants a /32 pinned to the physical adapter. See <see cref="PinLedger"/>.</summary>
public enum PinOwner
{
    /// <summary>The home relay, or the way into it the home tunnel is on.</summary>
    Relay,

    /// <summary>Every way into a relay a tunnel is on - home's and each other tunnel's - so a Probe down one never takes a game route.</summary>
    Doors,

    /// <summary>The way each other tunnel is on (multi-tunnel).</summary>
    Paths,
}

/// <summary>
/// Which /32s are pinned, and for whom. Pure bookkeeping: RouteManager installs what <see cref="Set"/> says to add
/// and deletes what it says to remove, and nothing else.
///
/// Before 2026-09-30 each kind of pin kept its own list and stepped round the others: a door already pinned as the
/// relay was "taken over", a path already pinned as a door was skipped. That held while only home had doors. With a
/// tunnel to another relay switching ways in too, one address is routinely several things at once - vn-1 is home for
/// a Naraka player AND the entry vn-1-sg in front of that player's Singapore tunnel - and the lists could not say who
/// still needed it: dropping the doors of a relay deleted the /32 a live tunnel was using, and pinning a door that was
/// already a path deleted and re-added it under a running match.
///
/// The rule now: an address is pinned while ANY owner wants it, it is added when the first one does and deleted when
/// the last one lets go - never in between, so a live tunnel's address is never unpinned for a moment.
/// </summary>
public sealed class PinLedger
{
    private readonly Dictionary<string, HashSet<PinOwner>> _owners = new(StringComparer.Ordinal);

    /// <summary>
    /// Replaces what <paramref name="owner"/> wants with <paramref name="prefixes"/>. Returns the prefixes nobody wanted
    /// before and one owner does now (to install), and those somebody wanted before and nobody does now (to delete).
    /// A prefix is never in both.
    /// </summary>
    public (List<string> Add, List<string> Remove) Set(PinOwner owner, IEnumerable<string> prefixes)
    {
        var wanted = prefixes.ToHashSet(StringComparer.Ordinal);
        var add = new List<string>();
        var remove = new List<string>();

        foreach (var (prefix, owners) in _owners.ToList())
        {
            if (wanted.Contains(prefix) || !owners.Remove(owner) || owners.Count > 0) continue;
            _owners.Remove(prefix);
            remove.Add(prefix);
        }
        foreach (var prefix in wanted)
        {
            if (!_owners.TryGetValue(prefix, out var owners))
            {
                _owners[prefix] = owners = [];
                add.Add(prefix);
            }
            owners.Add(owner);
        }
        return (add, remove);
    }

    /// <summary>Everything, owners and all, forgotten. Returns every prefix that was pinned, to delete.</summary>
    public List<string> Clear()
    {
        var all = _owners.Keys.ToList();
        _owners.Clear();
        return all;
    }

    public bool IsPinned(string prefix) => _owners.ContainsKey(prefix);

    public bool IsOwnedBy(string prefix, PinOwner owner) => _owners.TryGetValue(prefix, out var o) && o.Contains(owner);

    /// <summary>Wanted by <paramref name="owner"/> and nobody else - the only case where re-pinning it disturbs no other tunnel.</summary>
    public bool IsOwnedOnlyBy(string prefix, PinOwner owner) =>
        _owners.TryGetValue(prefix, out var o) && o.Count == 1 && o.Contains(owner);

    /// <summary>How many prefixes any of <paramref name="owners"/> wants.</summary>
    public int CountOwnedByAny(params PinOwner[] owners) => _owners.Values.Count(o => owners.Any(o.Contains));

    public IReadOnlyCollection<string> Prefixes => _owners.Keys;

    /// <summary>What <paramref name="owner"/> wants now.</summary>
    public List<string> OwnedBy(PinOwner owner) => [.. _owners.Where(kv => kv.Value.Contains(owner)).Select(kv => kv.Key)];
}
