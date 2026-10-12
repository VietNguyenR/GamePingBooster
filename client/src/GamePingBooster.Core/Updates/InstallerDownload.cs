using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GamePingBooster.Core.Net;

namespace GamePingBooster.Core.Updates;

/// <summary>
/// Downloads the setup .exe of a newer release into a folder and checks it against the SHA-256 GitHub
/// computed when the release was uploaded (ReleaseFeed.FindInstaller). Shared by the app's manual update
/// window and the service's AutoUpdater; each chooses its own folder.
///
/// The file is run elevated, so it is never kept unchecked: one that does not hash right is deleted
/// rather than kept for a retry, and a half-finished download is never resumed into.
/// </summary>
public static class InstallerDownload
{
    /// <summary>A download that receives nothing for this long has stalled, not slowed.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Far above any real installer; only here so a bad size cannot fill the disk.</summary>
    public const long MaxSize = 300L * 1024 * 1024;

    /// <summary>The name ./gpb release gives the setup of a version.</summary>
    public static string FileName(string version) => $"GamePingBooster-Setup-{version}.exe";

    /// <summary>
    /// Downloads into <paramref name="folder"/>, which must already exist and belong to the caller, and
    /// returns the file's path once its hash matches. Progress reports bytes received so far. Throws on
    /// network failure, cancellation or a hash mismatch.
    /// </summary>
    public static async Task<string> DownloadAsync(
        AvailableUpdate update, string folder, string? userAgentVersion, IProgress<long>? progress, CancellationToken ct)
    {
        if (update.InstallerUrl is not { } url || update.InstallerSha256 is not { } expected)
        {
            throw new InvalidOperationException("This release has no installer to download.");
        }
        if (update.InstallerSize is > MaxSize)
        {
            throw new InvalidDataException("The installer is larger than expected.");
        }

        var finalPath = Path.Combine(folder, FileName(update.Version));
        var partPath = finalPath + ".partial";

        // No overall timeout: a 20 MB file on a slow line can legitimately take minutes. A stall is
        // what is caught instead - see StallTimeout.
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GamePingBooster", userAgentVersion ?? "0.0.0"));

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
                    progress?.Report(received);
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

    /// <summary>The SHA-256 of a file on disk, lower-case hex.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }
}
