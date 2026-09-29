namespace GamePingBooster.Core.Profiles;

/// <summary>
/// The pure half of the app's profile sync (GamePingBooster.App/Services/ProfileSync.cs), apart from HTTP and the pipe so
/// PathCheck can hold it.
/// </summary>
public static class ProfileSyncPlan
{
    /// <summary>More than any catalogue will have, and a bound on what one answer can make the app do.</summary>
    public const int MaxGames = 256;

    /// <summary>
    /// The envelopes to hand to the service from a POST /profiles answer: every "sealed" entry that carries one, once
    /// per game, in the answer's order. "unchanged" and "limited" entries are the service keeping what it has; anything
    /// unknown is treated the same way, never as a reason to drop a working profile.
    /// </summary>
    public static List<string> EnvelopesToStore(IEnumerable<(string Game, string Status, string? Envelope)> answer)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var envelopes = new List<string>();
        foreach (var (game, status, envelope) in answer)
        {
            if (envelopes.Count >= MaxGames) break;
            if (!status.Equals("sealed", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(envelope)) continue;
            if (!IsGameCode(game) || !seen.Add(game.ToLowerInvariant())) continue;
            envelopes.Add(envelope);
        }
        return envelopes;
    }

    /// <summary>
    /// The games to ask GET /profile for after <paramref name="first"/>, against a server older than POST /profiles:
    /// every game its list names, each once. There used to be a cap of eight here - and with ten games on the server the
    /// tenth (wot) was never fetched, and its stale file made the start-up sync run on every launch (2026-09-29).
    /// </summary>
    public static List<string> LegacyGamesAfter(string first, IEnumerable<string>? available) =>
        (available ?? [])
            .Select(code => code.Trim().ToLowerInvariant())
            .Where(IsGameCode)
            .Where(code => code != first)
            .Distinct()
            .Take(MaxGames)
            .ToList();

    /// <summary>What the service reported holding, cleaned into a POST /profiles `have`: well-formed entries only.</summary>
    public static Dictionary<string, string> Have(IReadOnlyDictionary<string, string>? held)
    {
        var have = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (game, hash) in held ?? new Dictionary<string, string>())
        {
            if (have.Count >= MaxGames) break;
            var code = game.Trim().ToLowerInvariant();
            if (IsGameCode(code) && hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit)) have[code] = hash.ToLowerInvariant();
        }
        return have;
    }

    private static bool IsGameCode(string code) =>
        code.Length is > 0 and <= 32 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
