using System.ComponentModel;
using System.Diagnostics;
using GamePingBooster.Core.Updates;

namespace GamePingBooster.App.Services;

/// <summary>
/// Downloads the setup .exe of a newer release, checks it, and runs it - the MANUAL way, from the update
/// window, with the UAC prompt. Since 0.5.0 the service installs releases by itself without one
/// (AutoUpdater in gpb-service); this stays for when it cannot: a release it refused, or a service too
/// old to have it.
///
/// There is no updater of its own here, on purpose. The installer already does everything an
/// update needs and does it in the right order - closes the app, stops the service and waits for
/// it to let go of its files, recreates the service, reinstalls the driver and firewall rule - and
/// a second copy of those steps would drift from the first the day either changed. So an update is
/// exactly an install over the top, just started from inside the app.
///
/// The file is run elevated, so it is never run unchecked: it has to hash to the SHA-256 GitHub
/// computed when the release was uploaded (see ReleaseFeed.FindInstaller), and a file that does
/// not is deleted rather than kept for a retry.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Downloads the installer into %TEMP% and returns its path once its hash matches. Progress
    /// reports bytes received so far. Throws on network failure, cancellation or a hash mismatch.
    /// </summary>
    public static Task<string> DownloadAsync(AvailableUpdate update, IProgress<long> progress, CancellationToken ct)
    {
        // A folder of our own, emptied first: a half-finished download from last time is not
        // something to resume into a file that will be run as administrator.
        var folder = Path.Combine(Path.GetTempPath(), "GamePingBooster-Update");
        try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
        Directory.CreateDirectory(folder);

        return InstallerDownload.DownloadAsync(update, folder, UpdateChecker.CurrentVersion(), progress, ct);
    }

    /// <summary>
    /// Starts the installer and waits for it to end, returning its exit code - or null when it
    /// could not be started at all.
    ///
    /// Started UNELEVATED, and that matters: Inno Setup then raises the UAC prompt itself and keeps
    /// the original, unelevated process to start the app again afterwards (runasoriginaluser in
    /// GamePingBooster.iss). Starting it with "runas" from here would bring the app back running
    /// as administrator.
    ///
    /// When the install goes ahead, setup closes this app before it copies anything, so this call
    /// never returns. It returns when setup ended without installing - most often because the
    /// person answered No to the Windows prompt - and the app, still running, can say so.
    /// </summary>
    public static async Task<int?> RunAsync(string installerPath)
    {
        var info = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            // /SILENT shows setup's progress window but no wizard pages; /SP- skips the "this will
            // install" prompt; /RELAUNCH=1 is ours, and starts the app again when setup is done.
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /RELAUNCH=1",
        };

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception)
        {
            // 1223 (the operation was cancelled by the user) and anything else that stops it starting.
            return null;
        }
        if (process is null) return null;

        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
