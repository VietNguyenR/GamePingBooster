using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace GamePingBooster.App.Services.Localization;

/// <summary>Which language the UI is in. Two for now; adding a third is a table, not a change here.</summary>
public enum AppLanguage
{
    English,
    Vietnamese,
}

/// <summary>
/// Every word on screen, in the language the person picked.
///
/// Plain C# dictionaries rather than .resx, and that is not a shortcut. .resx compiles to
/// satellite assemblies which are loaded at runtime, and Native AOT loads no assemblies at
/// runtime - the strings would simply not be there in the shipped build. InvariantGlobalization is
/// on as well (both .csproj files), so anything that looks up resources or formats by CultureInfo
/// is out too. A dictionary and string.Format with the invariant culture work in every build.
///
/// The XAML side goes through the application's resources: a language is one ResourceDictionary,
/// swapping it is what changes the language, and every {DynamicResource} on screen follows the
/// swap. So switching language needs no restart and no window is rebuilt.
///
/// The C# side calls <see cref="T"/> and <see cref="F"/>. View models that hold text have to raise
/// their own PropertyChanged when <see cref="Changed"/> fires - a binding to StatusText cannot
/// know that the words behind it moved.
/// </summary>
public static class Loc
{
    private static readonly IReadOnlyDictionary<string, string> English = StringsEn.Values;

    private static IReadOnlyDictionary<string, string> _current = English;
    private static ResourceDictionary? _applied;

    /// <summary>The language in use. Changing it goes through <see cref="Set"/>.</summary>
    public static AppLanguage Current { get; private set; } = AppLanguage.English;

    /// <summary>Raised after the language changed, on whichever thread changed it (the UI one).</summary>
    public static event Action? Changed;

    /// <summary>
    /// Applies the saved language, or - on a machine that has never chosen one - the language
    /// Windows itself is in. Called once, before the first window exists.
    /// </summary>
    public static void Initialize(Application app) => Apply(app, LanguageStore.Load() ?? WindowsLanguage());

    /// <summary>Switches language and remembers the choice for next time.</summary>
    public static void Set(AppLanguage language)
    {
        if (language == Current) return;
        if (Application.Current is { } app) Apply(app, language);
        LanguageStore.Save(language);
    }

    private static void Apply(Application app, AppLanguage language)
    {
        Current = language;
        _current = language == AppLanguage.Vietnamese ? StringsVi.Values : English;

        // A dictionary built from the table rather than one written in XAML: the tables are the
        // one place a translator has to look, and two copies of 250 strings would not stay equal.
        var dictionary = new ResourceDictionary();
        foreach (var (key, value) in _current) dictionary.Add(key, value);

        // Removed and added rather than edited in place, because it is the collection change that
        // tells everything bound with {DynamicResource} to look its key up again.
        if (_applied is not null) app.Resources.MergedDictionaries.Remove(_applied);
        app.Resources.MergedDictionaries.Add(dictionary);
        _applied = dictionary;

        Changed?.Invoke();
    }

    /// <summary>
    /// The string for <paramref name="key"/>. Falls back to English, then to the key itself: a
    /// missing translation must look like a missing translation, never like an empty label.
    /// </summary>
    public static string T(string key) =>
        _current.TryGetValue(key, out var value) ? value
        : English.TryGetValue(key, out var fallback) ? fallback
        : key;

    /// <summary>Whether this build has words for <paramref name="key"/> at all.</summary>
    public static bool Has(string key) => English.ContainsKey(key);

    /// <summary>
    /// The string for <paramref name="key"/> with {0}, {1}... filled in. Invariant culture, which
    /// is the only one a build with InvariantGlobalization has, and the right one for numbers this
    /// app prints - "43 ms" reads the same everywhere.
    /// </summary>
    public static string F(string key, params object?[] args)
    {
        var format = T(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            // A translation with a stray brace must not take the window down with it.
            return format;
        }
    }

    /// <summary>
    /// What language Windows itself is in, reduced to what this app has. Vietnamese Windows gets a
    /// Vietnamese app on first run; everything else gets English.
    ///
    /// The Win32 call rather than CultureInfo.CurrentUICulture: with InvariantGlobalization the
    /// framework reports the invariant culture whatever the machine is set to.
    /// </summary>
    private static AppLanguage WindowsLanguage()
    {
        try
        {
            // The low 10 bits of a LANGID are the primary language. 0x2A is Vietnamese.
            return (GetUserDefaultUILanguage() & 0x3FF) == 0x2A ? AppLanguage.Vietnamese : AppLanguage.English;
        }
        catch (Exception)
        {
            return AppLanguage.English;
        }
    }

    // DllImport rather than LibraryImport: the generated marshalling code for LibraryImport needs
    // AllowUnsafeBlocks, and this call has nothing to marshal - no arguments and a blittable return.
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
