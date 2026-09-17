using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GamePingBooster.Core.Net;

namespace GamePingBooster.App.Services;

/// <summary>
/// Downloads the setup .exe of a newer release, checks it, and runs it.
///
/// There is no updater of its own here, on purpose. The installer already does everything an
/// update needs and does it in the right order - closes the app, stops the service and waits for
/// it to let go of its files, recreates the service, reinstalls the driver and firewall rule - and
/// a second copy of those steps would drift from the first the day either changed. So an update is
/// exactly an install over the top, just started from inside the app.
///
/// The file is run elevated, so it is never run unchecked: it has to hash to the SHA-256 GitHub
/// computed when the release was uploaded (see UpdateChecker.FindInstaller), and a file that does
/// not is deleted rather than kept for a retry.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>A download that receives nothing for this long has stalled, not slowed.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Far above any real installer; only here so a bad size cannot fill the disk.</summary>
    private const long MaxSize = 300L * 1024 * 1024;

    /// <summary>
    /// Downloads the installer into %TEMP% and returns its path once its hash matches. Progress
    /// reports bytes received so far. Throws on network failure, cancellation or a hash mismatch.
    /// </summary>
    public static async Task<string> DownloadAsync(
        AvailableUpdate update, IProgress<long> progress, CancellationToken ct)
    {
        if (update.InstallerUrl is not { } url || update.InstallerSha256 is not { } expected)
        {
            throw new InvalidOperationException("This release has no installer to download.");
        }
        if (update.InstallerSize is > MaxSize)
        {
            throw new InvalidDataException("The installer is larger than expected.");
        }

        // A folder of our own, emptied first: a half-finished download from last time is not
        // something to resume into a file that will be run as administrator.
        var folder = Path.Combine(Path.GetTempPath(), "GamePingBooster-Update");
        try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
        Directory.CreateDirectory(folder);

        var finalPath = Path.Combine(folder, $"GamePingBooster-Setup-{update.Version}.exe");
        var partPath = finalPath + ".partial";

        // No overall timeout: a 17 MB file on a slow line can legitimately take minutes. A stall is
        // what is caught instead - see StallTimeout.
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("GamePingBooster", UpdateChecker.CurrentVersion() ?? "0.0.0"));

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        try
        {
            // GitHub answers with a redirect to its storage host, which HttpClient follows.
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    received += read;
                    if (received > MaxSize) throw new InvalidDataException("The installer is larger than expected.");

                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    progress.Report(received);
                }
            }

            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(expected)))
            {
                throw new InvalidDataException(
                    "The downloaded installer does not match the release. It was deleted and nothing was installed.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(partPath);
            throw new TimeoutException("The download stopped receiving data.");
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }
}
