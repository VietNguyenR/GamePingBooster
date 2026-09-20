namespace GamePingBooster.Service.Dns;

/// <summary>
/// Which names this resolver answers itself, and - just as deliberately - which it refuses to.
///
/// Measured on 2026-09-20 with <c>./gpb blockcheck</c> on a VNPT line: the ISP's own resolvers
/// answer <c>steamcommunity.com</c> and <c>store.steampowered.com</c> with <b>127.0.0.1</b>, while
/// the same questions over DoH return addresses that serve a valid Steam certificate. Nothing else
/// Steam needs was touched, no on-path injection was seen, and no address was blocked. So the fix
/// is a resolver, not a tunnel.
///
/// THE CONTENT NAMES ARE NOT HERE, AND THAT IS THE POINT. The same run measured Steam's content
/// CDNs resolving to caches inside Vietnam - 27.77.81.x, 203.113.182.x, 125.234.51.x - that connect
/// in 10-20 ms, against 30-43 ms for the addresses a foreign resolver hands out. For downloads the
/// ISP's answer is the BETTER one. Sending those names through an encrypted resolver abroad would
/// make every player's downloads slower while fixing nothing, which is a worse outcome than not
/// shipping the feature. They stay on the ISP's path, answered by the ISP.
///
/// The list is small and hard-coded for now. It belongs in the profile the licence server delivers,
/// next to the relay list, so a newly blocked name does not need a client release - but a profile
/// field is a schema change on both sides, and the first version of this has to earn that first.
/// </summary>
internal static class SteamNames
{
    /// <summary>
    /// Matched as suffixes, so <c>steamcommunity.com</c> covers <c>www.steamcommunity.com</c> and
    /// every other label under it.
    ///
    /// <c>steampowered.com</c> as a whole rather than the two names measured: <c>store</c> and the
    /// apex were poisoned while <c>api</c>, <c>login</c> and <c>help</c> were not, and treating the
    /// three clean ones as scoped costs one encrypted query each and survives the ISP extending
    /// its list. It does NOT cover the content names, which live under steamstatic.com.
    /// </summary>
    private static readonly string[] Scoped =
    [
        "steamcommunity.com",
        "steampowered.com",
        "steamgames.com",
        "steam-chat.com",
        "valvesoftware.com",
    ];

    /// <summary>
    /// Names that must never be scoped even though they sit under a scoped suffix.
    ///
    /// <c>media.steampowered.com</c> is the trap: it is part of the content path and resolves to
    /// the in-country cache, but its suffix is one this resolver claims. Without this exception the
    /// feature would quietly slow down store media for everyone.
    /// </summary>
    private static readonly string[] Excluded =
    [
        "media.steampowered.com",
        "steamcontent.com",
        "steamstatic.com",
    ];

    /// <summary>The namespaces the policy hands to Windows. A leading dot is what NRPT expects for a suffix.</summary>
    public static IReadOnlyList<string> Namespaces => [.. Scoped.Select(s => "." + s)];

    public static IReadOnlyList<string> ScopedSuffixes => Scoped;

    /// <summary>
    /// True when this resolver should answer the name over DoH rather than pass it to the ISP.
    /// The name is expected lower-cased and without a trailing dot, as
    /// <see cref="Core.Net.DnsWire.TryReadQuestion"/> produces it.
    /// </summary>
    public static bool IsScoped(string name)
    {
        foreach (var excluded in Excluded)
        {
            if (Matches(name, excluded)) return false;
        }

        foreach (var scoped in Scoped)
        {
            if (Matches(name, scoped)) return true;
        }

        return false;
    }

    /// <summary>
    /// The name the policy check resolves to prove the rule took effect.
    ///
    /// It has to be one the ISP is known to poison, or a correct answer proves nothing: any clean
    /// name returns the same addresses whichever resolver answered it.
    /// </summary>
    public const string Canary = "steamcommunity.com";

    private static bool Matches(string name, string suffix) =>
        name.Equals(suffix, StringComparison.Ordinal) ||
        (name.Length > suffix.Length &&
         name[name.Length - suffix.Length - 1] == '.' &&
         name.EndsWith(suffix, StringComparison.Ordinal));
}
