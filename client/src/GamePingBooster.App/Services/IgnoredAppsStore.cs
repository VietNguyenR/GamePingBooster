namespace GamePingBooster.App.Services;

/// <summary>
/// The background apps the player told the "Tài nguyên máy" line to stop naming ("Bỏ qua"): tools they keep open on
/// purpose, DisplayFusion on the owner's PC (2026-10-17). A list the player builds, because the app cannot know which
/// of their apps they need - and no list we could ship would cover them.
///
/// Per person, beside the panel state in %LOCALAPPDATA% (see <see cref="ChecklistStore"/>). One app per line:
/// process name, a tab, the name shown. Anything unreadable is an empty list: every app is named again, nothing breaks.
/// </summary>
public static class IgnoredAppsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster", "checklist-ignored-apps");

    /// <summary>Process name to the name shown for it.</summary>
    public static Dictionary<string, string> Load()
    {
        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split('\t');
                if (parts[0].Trim() is { Length: > 0 } key) apps[key] = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : key;
            }
        }
        catch (Exception)
        {
            // Nothing saved yet, or unreadable.
        }
        return apps;
    }

    public static void Save(IReadOnlyDictionary<string, string> apps)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, apps.Select(a => a.Key + "\t" + a.Value.Replace('\t', ' ')));
        }
        catch (Exception)
        {
            // Ignored for this run only.
        }
    }
}
