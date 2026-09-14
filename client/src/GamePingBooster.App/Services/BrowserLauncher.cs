using System.Diagnostics;
using Microsoft.Win32;

namespace GamePingBooster.App.Services;

/// <summary>
/// Opens a web page in a browser, even on a machine whose "default browser" setting is broken.
///
/// The obvious way - <c>Process.Start</c> with <c>UseShellExecute</c> - asks Windows which program
/// handles <c>https:</c> and fails outright when the answer points at nothing. That is not rare: a
/// default browser that was later uninstalled leaves the association behind, and every link on the
/// machine then fails with Win32 error 1155, which .NET reports as "Application not found". A
/// customer hit exactly that on the sign-in window on 2026-09-14, on a PC with a perfectly good
/// browser installed - Windows simply no longer knew which one to use.
///
/// So the shell is only the first attempt. After it come the browsers Windows has on record as
/// INSTALLED, launched directly by path, which does not consult the association at all. Edge ships
/// with every supported Windows, so in practice the second step always finds something.
/// </summary>
public static class BrowserLauncher
{
    /// <summary>
    /// Tries to open <paramref name="url"/>. Never throws; false means no browser could be started,
    /// and the caller should put the address on screen for the person to open themselves.
    /// </summary>
    public static bool TryOpen(string url)
    {
        // http and https only. Everything below hands the string to an executable, and a browser
        // happily accepts a file path or a command-line switch as its first argument.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        if (TryShell(uri.AbsoluteUri)) return true;

        foreach (var browser in InstalledBrowsers())
        {
            if (TryDirect(browser, uri.AbsoluteUri)) return true;
        }

        return false;
    }

    private static bool TryShell(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryDirect(string executable, string url)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            // ArgumentList, not a joined string: the URL is quoted correctly whatever it contains,
            // and cannot be split into extra arguments by a space or a quote.
            start.ArgumentList.Add(url);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Browser executables Windows has registered as installed, existing on disk, without repeats.
    ///
    /// <c>Clients\StartMenuInternet</c> is where every browser installer records itself - it is the
    /// list the Default Apps page is built from - and it survives a broken <c>https</c>
    /// association, which is the whole point. App Paths is the fallback for an installer that
    /// skipped it.
    /// </summary>
    private static IEnumerable<string> InstalledBrowsers()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in Read(() => RegisteredBrowsers(hive)) ?? [])
            {
                if (seen.Add(path)) yield return path;
            }
        }

        foreach (var exe in new[] { "msedge.exe", "chrome.exe", "firefox.exe" })
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                var path = Read(() => ExistingExecutable(
                    hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}")
                        ?.GetValue(null) as string));
                if (path is not null && seen.Add(path)) yield return path;
            }
        }
    }

    private static List<string> RegisteredBrowsers(RegistryKey hive)
    {
        var found = new List<string>();
        using var clients = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
        if (clients is null) return found;

        foreach (var name in clients.GetSubKeyNames())
        {
            // Internet Explorer is still registered on every Windows install. On Windows 10 it is
            // the real thing and cannot render the sign-in page; on 11 it only forwards to Edge,
            // which App Paths below reaches directly anyway.
            if (name.Equals("IEXPLORE.EXE", StringComparison.OrdinalIgnoreCase)) continue;

            using var command = clients.OpenSubKey($@"{name}\shell\open\command");
            var path = ExistingExecutable(command?.GetValue(null) as string);
            if (path is not null) found.Add(path);
        }
        return found;
    }

    /// <summary>
    /// The executable at the start of a registry command line, if it exists.
    ///
    /// Commands are stored as <c>"C:\Program Files\...\chrome.exe" --flag</c> or unquoted. A
    /// registered path that no longer exists is precisely the uninstalled-browser case, so it is
    /// skipped rather than launched into a second "Application not found".
    /// </summary>
    internal static string? ExistingExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());

        string path;
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            if (close < 0) return null;
            path = command[1..close];
        }
        else
        {
            var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe < 0 ? command : command[..(exe + 4)];
        }

        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? path
            : null;
    }

    /// <summary>A registry read that fails - a locked-down machine, a key with odd permissions - is an empty answer.</summary>
    private static T? Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
