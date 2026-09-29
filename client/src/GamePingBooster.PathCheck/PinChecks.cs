using GamePingBooster.Core.Paths;

namespace GamePingBooster.PathCheck;

/// <summary>
/// Who owns a pinned /32 (PinLedger, 2026-09-30). The one thing that must never happen: a tunnel's address unpinned,
/// even for a moment, while that tunnel still uses it - so a probe or a game route could take it into the tunnel.
/// </summary>
internal static partial class Program
{
    private const string Vn1 = "222.255.184.166/32";
    private const string Sg1 = "139.99.73.90/32";
    private const string Vn2 = "103.82.21.251/32";

    private static void PinChecks()
    {
        HomeRelayAndAnEntryOnOneAddressStayPinned();
        APathThatIsAlsoADoorIsNotReAdded();
        TheRelayMovingKeepsADoorsAddress();
        EverythingGoesWhenEveryoneLetsGo();
        PinsHoldForEveryRandomSequence();
        EveryWayADecisionNamesIsFound();
    }

    /// <summary>
    /// The engine turns a switch decision's way id into the relay whose tunnel moves (RequestDoorMove). The ids are
    /// relays AND entries, and entries live inside their relay - the rig caught every move onto one being refused.
    /// </summary>
    private static void EveryWayADecisionNamesIsFound()
    {
        List<GamePingBooster.Core.Profiles.RelayEntry> relays =
        [
            new() { Id = "sg-1", Endpoint = "139.99.73.90:51820", Entries = [new() { Id = "vn-1-sg", Endpoint = "222.255.184.166:51820" }] },
            new() { Id = "vn-2", Endpoint = "103.82.21.251:51824" },
        ];
        var entry = GamePingBooster.Core.Profiles.RelayPaths.Find(relays, "vn-1-sg");
        var relay = GamePingBooster.Core.Profiles.RelayPaths.Find(relays, "SG-1");
        var none = GamePingBooster.Core.Profiles.RelayPaths.Find(relays, "hk-2");
        Check("A decision's way is found whether it is an entry (vn-1-sg -> relay sg-1) or a relay (sg-1), and nothing else is",
            entry is not null && GamePingBooster.Core.Profiles.RelayPaths.RelayIdOf(entry) == "sg-1" &&
            relay is not null && relay.ViaRelayId is null && relay.Id == "sg-1" && none is null,
            $"entry {entry?.Id}/{entry?.ViaRelayId}, relay {relay?.Id}, none {none?.Id}");
    }

    /// <summary>
    /// Naraka from Hanoi: home is vn-1, and the Singapore tunnel's ways in are sg-1 and vn-1-sg - the same address as
    /// home. Singapore's tunnel closing drops its doors, and must not take home's address with it.
    /// </summary>
    private static void HomeRelayAndAnEntryOnOneAddressStayPinned()
    {
        var ledger = new PinLedger();
        var (add1, _) = ledger.Set(PinOwner.Relay, [Vn1]);
        var (add2, _) = ledger.Set(PinOwner.Doors, [Vn1, Sg1]);
        var (_, remove) = ledger.Set(PinOwner.Doors, []);
        Check("vn-1 pinned once as home, then as an entry: added once, and the doors letting go leave it pinned",
            add1.SequenceEqual([Vn1]) && add2.SequenceEqual([Sg1]) && remove.SequenceEqual([Sg1]) && ledger.IsPinned(Vn1),
            $"add1 [{string.Join(",", add1)}] add2 [{string.Join(",", add2)}] remove [{string.Join(",", remove)}]");
    }

    private static void APathThatIsAlsoADoorIsNotReAdded()
    {
        var ledger = new PinLedger();
        ledger.Set(PinOwner.Doors, [Sg1, Vn1]);
        var (add, remove) = ledger.Set(PinOwner.Paths, [Vn1]);
        var (_, afterDoors) = ledger.Set(PinOwner.Doors, [Sg1]);
        Check("A tunnel's way that is already pinned as a door: nothing added, and the doors dropping it leave it for the tunnel",
            add.Count == 0 && remove.Count == 0 && afterDoors.Count == 0 && ledger.IsOwnedOnlyBy(Vn1, PinOwner.Paths),
            $"add {add.Count} remove {remove.Count} afterDoors {afterDoors.Count}");
    }

    private static void TheRelayMovingKeepsADoorsAddress()
    {
        var ledger = new PinLedger();
        ledger.Set(PinOwner.Relay, [Sg1]);
        ledger.Set(PinOwner.Doors, [Sg1, Vn1]);
        var (add, remove) = ledger.Set(PinOwner.Relay, [Vn1]);
        Check("Home moving from sg-1 to its entry vn-1-sg: nothing added or deleted - both are ways in, pinned already",
            add.Count == 0 && remove.Count == 0 && ledger.IsPinned(Sg1) && ledger.IsPinned(Vn1));
    }

    private static void EverythingGoesWhenEveryoneLetsGo()
    {
        var ledger = new PinLedger();
        ledger.Set(PinOwner.Relay, [Vn1]);
        ledger.Set(PinOwner.Doors, [Vn1, Sg1]);
        ledger.Set(PinOwner.Paths, [Sg1, Vn2]);
        var removed = new List<string>();
        removed.AddRange(ledger.Set(PinOwner.Paths, []).Remove);
        removed.AddRange(ledger.Set(PinOwner.Doors, []).Remove);
        removed.AddRange(ledger.Set(PinOwner.Relay, []).Remove);
        Check("Each owner letting go deletes exactly the addresses nobody else wants, each once, and nothing is left",
            removed.Order().SequenceEqual(new[] { Vn1, Sg1, Vn2 }.Order()) && ledger.Prefixes.Count == 0,
            string.Join(",", removed));
    }

    /// <summary>
    /// A hundred thousand random changes of three owners over eight addresses, applied to a model routing table the
    /// way RouteManager applies them. After every change: the table holds exactly what some owner wants; nothing still
    /// wanted by anyone was deleted; nothing was added that was already there.
    /// </summary>
    private static void PinsHoldForEveryRandomSequence()
    {
        var rng = new Random(Seed + 40);
        var addresses = Enumerable.Range(1, 8).Select(i => $"198.51.100.{i}/32").ToArray();
        var owners = Enum.GetValues<PinOwner>();
        var ledger = new PinLedger();
        var wants = owners.ToDictionary(o => o, _ => new HashSet<string>());
        var table = new HashSet<string>();
        string? failure = null;

        for (var i = 0; i < 100_000 && failure is null; i++)
        {
            var owner = owners[rng.Next(owners.Length)];
            var set = addresses.Where(_ => rng.Next(3) == 0).ToHashSet();
            var before = wants.Values.SelectMany(w => w).ToHashSet();
            wants[owner] = set;
            var after = wants.Values.SelectMany(w => w).ToHashSet();

            var (add, remove) = ledger.Set(owner, set);
            if (add.Intersect(remove).Any()) failure = $"step {i}: a prefix both added and removed";
            foreach (var p in remove)
            {
                if (after.Contains(p)) failure ??= $"step {i}: {p} deleted while still wanted";
                if (!table.Remove(p)) failure ??= $"step {i}: {p} deleted but was not installed";
            }
            foreach (var p in add)
            {
                if (before.Contains(p)) failure ??= $"step {i}: {p} added though it was already pinned";
                if (!table.Add(p)) failure ??= $"step {i}: {p} added twice";
            }
            if (!table.SetEquals(after)) failure ??= $"step {i}: table [{string.Join(",", table)}] vs wanted [{string.Join(",", after)}]";
        }
        Check("Property, 100,000 random changes: pinned is exactly what someone wants, and nothing wanted is ever deleted",
            failure is null, $"seed {Seed}: {failure}");
    }
}
