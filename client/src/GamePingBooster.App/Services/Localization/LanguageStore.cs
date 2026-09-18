namespace GamePingBooster.App.Services.Localization;

/// <summary>
/// Remembers which language the person chose.
///
/// In %LOCALAPPDATA%, next to the refresh token, and NOT in the service's configuration: the
/// language belongs to the person using the machine, not to the machine. Two people sharing a PC
/// get their own, and the UI can write it without asking the service - it runs as an ordinary
/// user and %ProgramData% is not its to write.
///
/// Every failure is swallowed and answered with "no choice saved". A profile with no roaming
/// folder, a locked file, a disk that is full: none of them is worth an error box about a
/// preference, and the app falls back to the language Windows is in.
/// </summary>
public static class LanguageStore
{
    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster");

    private static string FilePath => Path.Combine(Directory, "language");

    public static AppLanguage? Load()
    {
        try
        {
            return File.ReadAllText(FilePath).Trim().ToLowerInvariant() switch
            {
                "vi" => AppLanguage.Vietnamese,
                "en" => AppLanguage.English,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(AppLanguage language)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, language == AppLanguage.Vietnamese ? "vi" : "en");
        }
        catch (Exception)
        {
            // The language still changed for this session; it just will not be remembered.
        }
    }
}
