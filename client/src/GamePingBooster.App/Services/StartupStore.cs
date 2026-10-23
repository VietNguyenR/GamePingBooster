using Microsoft.Win32;

namespace GamePingBooster.App.Services;

/// <summary>
/// Starting the app with Windows: a value under HKCU\...\CurrentVersion\Run that opens it in the tray
/// (<see cref="Argument"/>). On by default for everybody, existing installations included (owner, 2026-10-23),
/// and switched off in Settings or in Task Manager's Startup tab.
///
/// Only the UI: the service already starts with Windows on its own. And starting is all it does - the app never
/// connects by itself at startup, so a machine that boots with this on has the same routing as one without it.
///
/// HKCU, not HKLM: the toggle belongs to the person, and the app writes it without the service or elevation.
/// What was chosen is remembered beside the close choice in %LOCALAPPDATA% (see CloseChoiceStore), so "off" stays
/// off across updates, and no file at all is what "never chosen" looks like - the default is then written once.
///
/// Every failure is swallowed: a preference is not worth an error box.
/// </summary>
public static class StartupStore
{
    /// <summary>On the command line Windows starts the app with: open in the tray, no window.</summary>
    public const string Argument = "--startup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Task Manager's Startup tab does not delete the Run value: it writes this one beside it, first byte odd for disabled.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // The installer's uninstall step deletes this same name (GamePingBooster.iss) - keep the two the same.
    private const string ValueName = "GamePingBooster";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster", "start-with-windows");

    /// <summary>
    /// Called as the app opens. Never chosen: on, written now. Chosen on: written again, so the Run value always
    /// points at the exe that is running. Chosen off: nothing.
    ///
    /// Only for the installed copy under Program Files: a build run from a source tree must not put itself in the
    /// machine's startup on its first run.
    /// </summary>
    public static void ApplyAtStart()
    {
        try
        {
            if (!IsInstalledCopy()) return;
            var chosen = ReadChoice();
            if (chosen == false) return;
            WriteRunValue();
            if (chosen is null) SaveChoice(true);
        }
        catch (Exception)
        {
            // Tried again next time the app opens.
        }
    }

    /// <summary>Whether Windows will start the app: chosen, written, and not disabled in Task Manager.</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (ReadChoice() == false) return false;
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The Settings switch. On also clears a "disabled" left by Task Manager - the person has just asked for it.</summary>
    public static void Set(bool on)
    {
        try
        {
            if (on)
            {
                WriteRunValue();
            }
            else
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            {
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            SaveChoice(on);
        }
        catch (Exception)
        {
            // The switch shows what IsEnabled reads back, so a write that failed does not look like it worked.
        }
    }

    private static void WriteRunValue()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(ValueName, $"\"{exe}\" {Argument}");
    }

    private static bool IsInstalledCopy()
    {
        var exe = Environment.ProcessPath;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return !string.IsNullOrEmpty(exe) && !string.IsNullOrEmpty(programFiles) &&
               exe.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ReadChoice()
    {
        try
        {
            return File.ReadAllText(FilePath).Trim().ToLowerInvariant() switch
            {
                "on" => true,
                "off" => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SaveChoice(bool on)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, on ? "on" : "off");
    }
}
