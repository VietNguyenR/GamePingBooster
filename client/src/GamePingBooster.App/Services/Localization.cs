using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Avalonia.Platform;

namespace GamePingBooster.App.Services;

public enum AppLanguage
{
    Vietnamese,
    English
}

/// <summary>
/// Manages application-wide internationalization (i18n) by parsing XML language resources
/// (Resources/Languages/Strings.vi.xml and Strings.en.xml).
/// </summary>
public static class Localization
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster");
    private static readonly string LanguageFile = Path.Combine(SettingsDir, "language.txt");

    private static readonly Dictionary<string, string> ViStrings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> EnStrings = new(StringComparer.OrdinalIgnoreCase);

    static Localization()
    {
        LoadXml("vi", ViStrings);
        LoadXml("en", EnStrings);
    }

    private static void LoadXml(string lang, Dictionary<string, string> dict)
    {
        try
        {
            var uri = new Uri($"avares://GamePingBooster/Resources/Languages/Strings.{lang}.xml");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                var doc = XDocument.Load(stream);
                foreach (var el in doc.Descendants("String"))
                {
                    var key = el.Attribute("Key")?.Value;
                    if (!string.IsNullOrEmpty(key))
                    {
                        dict[key] = el.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load {lang} XML: {ex.Message}");
        }
    }

    public static AppLanguage CurrentLanguage { get; private set; } = DetectInitialLanguage();

    public static event Action? LanguageChanged;

    private static AppLanguage DetectInitialLanguage()
    {
        try
        {
            if (File.Exists(LanguageFile))
            {
                var text = File.ReadAllText(LanguageFile).Trim();
                if (text.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("English", StringComparison.OrdinalIgnoreCase))
                {
                    return AppLanguage.English;
                }
                if (text.Equals("vi", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase))
                {
                    return AppLanguage.Vietnamese;
                }
            }

            var culture = CultureInfo.CurrentUICulture.Name;
            if (culture.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
            {
                return AppLanguage.Vietnamese;
            }
        }
        catch
        {
        }

        return AppLanguage.Vietnamese;
    }

    public static void SetLanguage(AppLanguage language)
    {
        if (CurrentLanguage == language) return;
        CurrentLanguage = language;

        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(LanguageFile, language == AppLanguage.Vietnamese ? "vi" : "en");
        }
        catch
        {
        }

        LanguageChanged?.Invoke();
    }

    public static bool IsVietnamese => CurrentLanguage == AppLanguage.Vietnamese;

    /// <summary>
    /// Looks up a string by Key from the active language's XML resource file.
    /// </summary>
    public static string GetString(string key, params object[] args)
    {
        var dict = IsVietnamese ? ViStrings : EnStrings;
        if (!dict.TryGetValue(key, out var val))
        {
            if (!EnStrings.TryGetValue(key, out val))
            {
                return key;
            }
        }

        if (args.Length > 0)
        {
            try
            {
                return string.Format(val, args);
            }
            catch
            {
                return val;
            }
        }

        return val;
    }

    /// <summary>
    /// Shorthand alias for GetString.
    /// </summary>
    public static string T(string key, params object[] args) => GetString(key, args);

    /// <summary>
    /// Translates service detail strings using XML keys while preserving dynamic game/relay names.
    /// </summary>
    public static string LocalizeDetail(string? detail, string? gameName, string? relayName)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "";

        if (detail.StartsWith("Waiting for game to launch", StringComparison.OrdinalIgnoreCase) ||
            detail.StartsWith("Connected to", StringComparison.OrdinalIgnoreCase))
        {
            return GetString("Detail.WaitingForGame", gameName ?? "game");
        }
        if (detail.StartsWith("Optimizing", StringComparison.OrdinalIgnoreCase) ||
            detail.StartsWith("Accelerating", StringComparison.OrdinalIgnoreCase))
        {
            return GetString("Detail.Optimizing", gameName ?? "game", relayName ?? "Relay");
        }
        if (detail.Equals("Starting up...", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.StartingUp");
        if (detail.Equals("Disconnected.", StringComparison.OrdinalIgnoreCase) ||
            detail.Equals("Disconnected", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.Disconnected");
        if (detail.Equals("Disconnecting...", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.Disconnecting");
        if (detail.Equals("Resolving relay address...", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.ResolvingRelay");
        if (detail.Equals("Configuring network adapter...", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.ConfiguringAdapter");
        if (detail.Equals("Connecting to relay...", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.ConnectingToRelay");
        if (detail.Equals("Getting the latest server list...", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.GettingServerList");
        if (detail.Equals("Sending the request to the background service...", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.SendingRequest");
        if (detail.Equals("Choose your game from the dropdown above before connecting.", StringComparison.OrdinalIgnoreCase))
            return GetString("Detail.SelectGame");

        return detail;
    }
}
