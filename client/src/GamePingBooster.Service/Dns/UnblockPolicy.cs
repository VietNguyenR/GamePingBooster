using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// One service the resolver answers for: which suffixes it claims, which names under them it must
/// not, and the name it proves itself with.
/// </summary>
internal sealed record UnblockApp(
    string Id,
    string Name,
    IReadOnlyList<string> Scope,
    IReadOnlyList<string> Excluded,
    string Canary)
{
    /// <summary>The namespaces the Windows policy is given. A leading dot is what NRPT expects for a suffix.</summary>
    public IReadOnlyList<string> Namespaces => [.. Scope.Select(s => "." + s)];

    /// <summary>
    /// True when this service should answer the name over encrypted DNS rather than pass it to the
    /// ISP. Expected lower-cased and without a trailing dot, as DnsWire.TryReadQuestion produces it.
    /// </summary>
    public bool Claims(string name)
    {
        foreach (var excluded in Excluded)
        {
            if (Matches(name, excluded)) return false;
        }

        foreach (var suffix in Scope)
        {
            if (Matches(name, suffix)) return true;
        }

        return false;
    }

    private static bool Matches(string name, string suffix) =>
        name.Equals(suffix, StringComparison.Ordinal) ||
        (name.Length > suffix.Length &&
         name[name.Length - suffix.Length - 1] == '.' &&
         name.EndsWith(suffix, StringComparison.Ordinal));
}

/// <summary>
/// Every service this machine currently unblocks, and where the list came from.
///
/// It comes from the profile the licence server delivers. That is the whole point of this type:
/// the list used to be an array compiled into the service, so a newly blocked name meant a client
/// release - and the people who need the fix are running whatever build they installed months ago.
/// Now adding a service, or a name to one, is a row in a database and reaches every machine on its
/// next profile fetch.
///
/// <see cref="Builtin"/> is the fallback and not the source of truth. It exists for two cases that
/// are both real: a self-hosted installation with no licence server at all, and a client whose
/// profile came from a server that predates the field. In both, Steam alone is better than nothing
/// - it is the service this was built for and the one measured. A profile that DOES carry a list
/// replaces it entirely, including with an empty one, so switching the feature off for everybody
/// stays a server-side edit.
/// </summary>
internal sealed record UnblockPolicy(IReadOnlyList<UnblockApp> Apps, string Source)
{
    public bool IsEmpty => Apps.Count == 0;

    /// <summary>
    /// Steam, as measured on VNPT on 2026-09-20 and 2026-09-22.
    ///
    /// The exclusions are the part to read twice. media.steampowered.com sits under a claimed
    /// suffix and is content: it resolves to an in-country cache that connects in 10 ms, so
    /// claiming it would replace the fastest answer with a slower one and call that a fix.
    /// </summary>
    public static UnblockPolicy Builtin { get; } = new(
    [
        new UnblockApp(
            "steam",
            "Steam",
            ["steamcommunity.com", "steampowered.com", "steamgames.com", "steam-chat.com", "valvesoftware.com"],
            ["media.steampowered.com", "steamcontent.com", "steamstatic.com"],
            "steamcommunity.com"),
    ], "built in");

    public static UnblockPolicy Empty { get; } = new([], "none");

    /// <summary>
    /// Reads the list out of a delivered profile, dropping anything unusable rather than failing.
    ///
    /// A malformed row must not take the feature down for the rows that are fine: this data is
    /// edited on a website by a person, and the failure mode of strictness here is every player
    /// losing the fix because somebody left a field blank.
    /// </summary>
    public static UnblockPolicy FromProfile(ProfileBundle? profile, Action<string> log)
    {
        if (profile is null || profile.Unblock.Count == 0) return Builtin;

        var apps = new List<UnblockApp>();

        foreach (var entry in profile.Unblock)
        {
            var scope = Clean(entry.Scope);
            if (scope.Count == 0)
            {
                log($"Unblock: '{entry.Id}' claims no names - ignoring it.");
                continue;
            }

            var canary = Normalise(entry.Canary);
            if (canary.Length == 0)
            {
                // Refused rather than defaulted to the first scoped name. The canary is what proves
                // the fix works on this line, and one picked by this code would be a name nobody
                // checked is actually poisoned - a verification that always passes.
                log($"Unblock: '{entry.Id}' declares no canary - ignoring it, because it could not be verified.");
                continue;
            }

            apps.Add(new UnblockApp(
                entry.Id.Trim(),
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Id.Trim() : entry.Name.Trim(),
                scope,
                Clean(entry.Excluded),
                canary));
        }

        return apps.Count == 0
            ? Empty
            : new UnblockPolicy(apps, $"the profile ({apps.Count} service(s))");
    }

    /// <summary>Which service claims this name, or null. First match wins; the lists should not overlap.</summary>
    public UnblockApp? ClaimedBy(string name)
    {
        foreach (var app in Apps)
        {
            if (app.Claims(name)) return app;
        }

        return null;
    }

    public IReadOnlyList<string> AllNamespaces => [.. Apps.SelectMany(a => a.Namespaces).Distinct()];

    public string Describe() => Apps.Count == 0 ? "nothing" : string.Join(", ", Apps.Select(a => a.Name));

    private static List<string> Clean(IEnumerable<string> names) =>
        [.. names.Select(Normalise).Where(n => n.Length > 0).Distinct()];

    /// <summary>
    /// Lower-cased, trimmed, and with a leading dot or trailing dot removed.
    ///
    /// Both are things a person types into a form - ".steamcommunity.com" is how the Windows policy
    /// writes a suffix, so it is the natural thing to paste - and either would stop the name ever
    /// matching, silently.
    /// </summary>
    private static string Normalise(string? name)
    {
        var text = (name ?? "").Trim().Trim('.').ToLowerInvariant();
        return text;
    }
}
