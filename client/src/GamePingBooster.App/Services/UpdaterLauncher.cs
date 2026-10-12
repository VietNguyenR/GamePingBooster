using System.Diagnostics;
using System.Runtime.InteropServices;
using GamePingBooster.App.Services.Localization;

namespace GamePingBooster.App.Services;

/// <summary>
/// Starts the updater window, gpb-updater.exe (0.5.0): the window that shows an automatic update step by step,
/// because setup closes this app to replace its files and a window inside it would vanish halfway through.
///
/// Run from a copy in %TEMP%\GamePingBooster, never from the install folder: setup replaces the installed
/// gpb-updater.exe, and cannot while it is running. The copy is the user's own, run as the user, and only reads -
/// setup itself is started by the service as SYSTEM from a folder the user cannot write.
/// </summary>
public static class UpdaterLauncher
{
    private const string ExeName = "gpb-updater.exe";

    /// <summary>
    /// "check" as the app opens; "install" when the service offers an update ("ready"). Null when the window could not
    /// be started (a build without it beside the app, a %TEMP% that refuses the copy) - the caller then carries on as if
    /// there were no update window, which is how every build before 0.5.0 behaved.
    /// </summary>
    public static Process? Start(string mode)
    {
        try
        {
            var installed = Path.Combine(AppContext.BaseDirectory, ExeName);
            var app = Environment.ProcessPath;
            if (!File.Exists(installed) || app is null) return null;

            var exe = CopyToTemp(installed);
            if (exe is null) return null;

            var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lang = Loc.Current == AppLanguage.English ? "en" : "vi";
            var process = Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { "--mode", mode, "--since", since.ToString(), "--ui-pid", Environment.ProcessId.ToString(), "--app", app, "--lang", lang },
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            // So its window may come to the front: Windows lets the foreground process hand that right on, and only it.
            if (process is not null) AllowSetForegroundWindow((uint)process.Id);
            return process;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The installed exe copied to %TEMP%, unless an identical copy is there. Another name when one is running.</summary>
    private static string? CopyToTemp(string installed)
    {
        var folder = Path.Combine(Path.GetTempPath(), "GamePingBooster");
        Directory.CreateDirectory(folder);
        var source = new FileInfo(installed);
        foreach (var name in new[] { ExeName, $"gpb-updater-{Environment.ProcessId}.exe" })
        {
            var target = new FileInfo(Path.Combine(folder, name));
            try
            {
                if (target.Exists && target.Length == source.Length && target.LastWriteTimeUtc == source.LastWriteTimeUtc) return target.FullName;
                source.CopyTo(target.FullName, overwrite: true);
                return target.FullName;
            }
            catch (IOException)
            {
                // In use by a window still open from before: the next name.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    // DllImport rather than LibraryImport, as elsewhere in this app: see Loc.cs.
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
