using GamePingBooster.Core.Profiles;

namespace GamePingBooster.PathCheck;

/// <summary>ProfileSyncPlan - the app's side of POST /profiles, and the per-game path kept for an older server.</summary>
internal static partial class Program
{
    private static void ProfileSyncChecks()
    {
        var h1 = new string('a', 64);
        var answer = new (string, string, string?)[]
        {
            ("apex", "sealed", "aa01"),
            ("cs2", "unchanged", null),
            ("deltaforce", "limited", null),
            ("dota2", "sealed", ""),          // sealed with nothing in it: nothing to store
            ("lol", "sealed", "aa02"),
            ("apex", "sealed", "aa03"),       // the same game twice: the first wins
            ("../x", "sealed", "aa04"),       // not a game code
            ("naraka", "something-new", "aa05"),
        };
        var stored = ProfileSyncPlan.EnvelopesToStore(answer);
        Check("Only sealed entries with an envelope are stored, once per game, in order",
            stored.SequenceEqual(["aa01", "aa02"]), string.Join(",", stored));
        Check("unchanged, limited and an unknown status keep what the service has - nothing to push",
            !stored.Contains("aa05"));
        Check("An answer that changed nothing stores nothing (the push still marks the sync)",
            ProfileSyncPlan.EnvelopesToStore([("pubg", "unchanged", null)]).Count == 0);

        var ten = new[] { "apex", "cs2", "deltaforce", "dota2", "lol", "naraka", "pubg", "tft", "valorant", "wot" };
        var legacy = ProfileSyncPlan.LegacyGamesAfter("pubg", ten);
        Check("Against an old server, every other game is fetched - wot too (the cap of eight left it out)",
            legacy.Count == 9 && legacy.Contains("wot") && !legacy.Contains("pubg"), string.Join(",", legacy));
        var many = Enumerable.Range(0, 400).Select(i => $"g{i}").ToList();
        Check($"...up to {ProfileSyncPlan.MaxGames}, a bound on what one answer can make the app do",
            ProfileSyncPlan.LegacyGamesAfter("pubg", many).Count == ProfileSyncPlan.MaxGames);
        Check("...each once, in lower case, junk dropped",
            ProfileSyncPlan.LegacyGamesAfter("pubg", ["CS2", "cs2", " cs2 ", "a/b", ""]).SequenceEqual(["cs2"]));

        var have = ProfileSyncPlan.Have(new Dictionary<string, string>
        {
            ["PUBG"] = h1.ToUpperInvariant(),
            ["cs2"] = "short",
            ["a b"] = h1,
            ["wot"] = h1,
        });
        Check("have: what the service holds, well-formed entries only, in lower case",
            have.Count == 2 && have["pubg"] == h1 && have["wot"] == h1, string.Join(",", have.Keys));
        Check("have from nothing is empty - the server sends everything", ProfileSyncPlan.Have(null).Count == 0);
    }
}
