using System.Text.Json;

namespace GamePingBooster.BlockCheck;

internal enum TargetKind
{
    /// <summary>Not blocked anywhere. If this one fails, the line or the tool is at fault, not the censor.</summary>
    Control,

    /// <summary>Login, API, the pages people open. Small traffic - the part worth fixing.</summary>
    Primary,

    /// <summary>Downloads and media. Measured separately because what is true of one app's bulk path is not true of another's.</summary>
    Bulk,
}

internal sealed record Target(string Host, TargetKind Kind, string Why);

internal sealed record TargetSet(string App, string DisplayName, string Path, Target[] All);

/// <summary>
/// Loads the names to probe from tools/blockcheck/targets.json.
///
/// They used to be an array compiled into this file, which was fine while there was one blocked app
/// and became the whole answer to "what do I put in the label?" the moment there were two: the list
/// was not selectable, so there was nowhere to say which app a run was about. Everything else in
/// this tool - the DNS probes, the handshakes, the verdicts - never knew which app it was looking at,
/// so the list was the only thing standing between it and any other blocked service.
///
/// The file is committed and found by walking up from the binary, exactly as ProtocolCheck finds its
/// vector file. Adding an app is then a data change with no build.
/// </summary>
internal static class Targets
{
    public const string DefaultApp = "steam";

    private const string RelativePath = "tools/blockcheck/targets.json";

    public static TargetSet Load(string app)
    {
        var path = Find();

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        if (!root.TryGetProperty("apps", out var apps))
        {
            throw new InvalidDataException($"{path} has no \"apps\" object.");
        }

        // Case-insensitively, because the app comes off a command line a human typed.
        var match = apps.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, app, StringComparison.OrdinalIgnoreCase));

        if (match.Value.ValueKind != JsonValueKind.Object)
        {
            var known = string.Join(", ", apps.EnumerateObject().Select(p => p.Name));
            throw new InvalidDataException($"{path} declares no app called '{app}'. It has: {known}.");
        }

        var targets = new List<Target>();

        // The control goes first and is not the app's to declare: every run needs one, and an app
        // list that could omit it could produce a report with nothing to check itself against.
        if (root.TryGetProperty("control", out var control))
        {
            targets.Add(new Target(
                Required(control, "host", path),
                TargetKind.Control,
                Optional(control, "why")));
        }

        if (!match.Value.TryGetProperty("targets", out var declared) ||
            declared.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path}: app '{match.Name}' has no \"targets\" array.");
        }

        foreach (var entry in declared.EnumerateArray())
        {
            var host = Required(entry, "host", path);
            var kind = Optional(entry, "kind").ToLowerInvariant() switch
            {
                "primary" => TargetKind.Primary,
                "bulk" => TargetKind.Bulk,
                var other => throw new InvalidDataException(
                    $"{path}: '{host}' has kind '{other}'. It must be \"primary\" or \"bulk\" - " +
                    "the verdict is read differently for each, so there is no sensible default."),
            };

            targets.Add(new Target(host, kind, Optional(entry, "why")));
        }

        if (targets.Count(t => t.Kind != TargetKind.Control) == 0)
        {
            throw new InvalidDataException($"{path}: app '{match.Name}' declares no names to probe.");
        }

        var displayName = match.Value.TryGetProperty("name", out var name) && name.GetString() is { } text
            ? text
            : match.Name;

        return new TargetSet(match.Name, displayName, path, [.. targets]);
    }

    public static string[] KnownApps()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Find()));
            return document.RootElement.TryGetProperty("apps", out var apps)
                ? [.. apps.EnumerateObject().Select(p => p.Name)]
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static string Label(TargetKind kind) => kind switch
    {
        TargetKind.Control => "control",
        TargetKind.Primary => "primary",
        TargetKind.Bulk => "bulk",
        _ => kind.ToString(),
    };

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {RelativePath} in any parent directory.");
    }

    private static string Required(JsonElement element, string property, string path) =>
        element.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"{path}: an entry is missing \"{property}\".");

    private static string Optional(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
}
