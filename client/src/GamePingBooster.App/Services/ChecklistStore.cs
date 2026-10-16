namespace GamePingBooster.App.Services;

/// <summary>
/// Remembers whether the "Kiểm tra máy" panel was left open or collapsed to its strip.
///
/// Per person, beside the language and the close choice in %LOCALAPPDATA%, for the reasons LanguageStore gives.
/// Unset, or anything unreadable, is open: the panel is new and a player has to see it once to know it is there.
/// </summary>
public static class ChecklistStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster", "checklist-panel");

    public static bool LoadOpen()
    {
        try
        {
            return File.ReadAllText(FilePath).Trim() != "collapsed";
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static void SaveOpen(bool open)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, open ? "open" : "collapsed");
        }
        catch (Exception)
        {
            // The panel still opens or closes; it just will not be remembered.
        }
    }
}
