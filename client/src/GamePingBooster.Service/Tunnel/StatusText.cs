namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// A line the service wants on the screen, said twice: once in English, and once as a key the UI
/// can look up in the language the person chose.
///
/// The service cannot pick the words. It runs as LocalSystem, it is one service for whoever is
/// logged in, and the language is a per-user setting the UI owns - so it names the line and hands
/// over the parts that vary. Relay names, game names and numbers travel as arguments because they
/// are not words in any language.
///
/// The English is not a fallback nobody reads: it is what goes in the service's log, and what an
/// older UI - or one that has never heard of this key - puts on screen. Both halves are written at
/// the same call site so they cannot describe different things.
/// </summary>
/// <param name="Key">A key in the UI's language tables, or "" for a line with no translation.</param>
/// <param name="English">The same line, in English, already formatted.</param>
public sealed record StatusText(string Key, string English)
{
    public StatusText(string key, string english, params string[] args) : this(key, english) =>
        Args = [.. args];

    /// <summary>The {0}, {1}... of <see cref="Key"/>, in order.</summary>
    public List<string> Args { get; init; } = [];
}
