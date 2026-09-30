namespace GamePingBooster.App.Services;

/// <summary>What closing the main window does: ask, go to the tray, or quit.</summary>
public enum CloseAction
{
    Ask,
    Tray,
    Quit,
}

/// <summary>
/// Remembers what closing the main window should do, once the person has ticked "remember" in the
/// close dialog or picked it in Settings. Unset is <see cref="CloseAction.Ask"/>.
///
/// Beside the language choice in %LOCALAPPDATA%, for the reasons LanguageStore gives: it belongs to
/// the person using the machine, and the UI writes it without the service. Every failure is
/// swallowed and read as "ask" - a preference is not worth an error box, and asking is the choice
/// that can never lose somebody's connection by surprise.
/// </summary>
public static class CloseChoiceStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster", "close-action");

    public static CloseAction Load()
    {
        try
        {
            return File.ReadAllText(FilePath).Trim().ToLowerInvariant() switch
            {
                "tray" => CloseAction.Tray,
                "quit" => CloseAction.Quit,
                _ => CloseAction.Ask,
            };
        }
        catch (Exception)
        {
            return CloseAction.Ask;
        }
    }

    public static void Save(CloseAction action)
    {
        try
        {
            if (action == CloseAction.Ask)
            {
                File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, action == CloseAction.Tray ? "tray" : "quit");
        }
        catch (Exception)
        {
            // Applies to this close; it just will not be remembered.
        }
    }
}
