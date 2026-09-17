using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using GamePingBooster.Core.Net;

namespace GamePingBooster.App.Services;

/// <summary>
/// Looks for a newer release on GitHub now and then, and says so. It never downloads or installs
/// anything itself: finding one puts a line in the main window's footer, and UpdateInstaller does
/// the rest only when the person presses it.
///
/// Why GitHub's API rather than something on the licence server: the release page IS where the
/// installer lives - ./gpb release publishes it there - so asking anything else would be a second
/// copy of "what is the latest version" that can disagree with the file people actually download.
/// /releases/latest also skips drafts and pre-releases on GitHub's side, so a release still being
/// built, or a beta, is never announced.
///
/// Deliberately quiet about failure. Offline, rate limited, GitHub down, a repository with no
/// release yet - every one of those means "no update to announce", and an update notice is not
/// worth an error message on a screen whose whole job is a Connect button.
/// </summary>
public sealed class UpdateChecker : IAsyncDisposable
{
    /// <summary>Where releases are published. The same repository ./gpb release pushes tags to.</summary>
    public const string Repository = "VietNguyenR/GamePingBooster";

    /// <summary>
    /// Every six hours. A release is not urgent, and unauthenticated calls to GitHub's API are
    /// limited to 60 an hour per address - an internet café full of players shares one.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>After start-up has settled, so the check never competes with the first connect.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);

    private static readonly string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    private readonly Action<AvailableUpdate> _onUpdate;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <param name="onUpdate">Called from a background thread when a newer release is found.</param>
    public UpdateChecker(Action<AvailableUpdate> onUpdate) => _onUpdate = onUpdate;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var update = await CheckAsync(CurrentVersion(), ct).ConfigureAwait(false);
                    if (update is not null) _onUpdate(update);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // See the class summary: no update to announce, and nothing worth saying.
                }

                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// The newest published release when it is newer than <paramref name="currentVersion"/>,
    /// otherwise null. Throws on network failure; the loop is what makes that quiet.
    /// </summary>
    public static async Task<AvailableUpdate?> CheckAsync(string? currentVersion, CancellationToken ct)
    {
        // A build with no version stamped is a development build: Directory.Build.props falls back
        // to 0.0.0 when there is no VERSION file. Announcing every release to it would be noise on
        // exactly the machine where nobody needs telling.
        if (!TryParseVersion(currentVersion, out var current) ||
            (current.Suffix.Length == 0 && current.Numbers.All(n => n == 0)))
        {
            return null;
        }

        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback },
            disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // GitHub's API refuses requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GamePingBooster", currentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http
            .GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct)
            .ConfigureAwait(false);

        // 404 is "no release published yet", which is not a failure.
        if (!response.IsSuccessStatusCode) return null;

        var release = await response.Content
            .ReadFromJsonAsync(UpdateJsonContext.Default.GitHubRelease, ct)
            .ConfigureAwait(false);
        if (release is null || release.Draft || release.Prerelease) return null;

        var latest = (release.TagName ?? "").Trim().TrimStart('v', 'V');
        if (!IsNewer(latest, currentVersion!)) return null;

        var installer = FindInstaller(release, latest);
        return new AvailableUpdate(latest, SafeReleaseUrl(release.HtmlUrl),
            installer?.Url, installer?.Sha256, installer?.Size);
    }

    /// <summary>
    /// The setup .exe of this release and its SHA-256, or null when either is missing or not what
    /// it should be - in which case the update falls back to opening the release page.
    ///
    /// Everything is checked against what ./gpb release publishes rather than trusted: the name is
    /// exactly GamePingBooster-Setup-{version}.exe, the URL is a download from THIS repository's
    /// tag, and the digest is GitHub's own sha256 of the uploaded file. The file is run elevated,
    /// so a response that does not match all three is not something to download.
    /// </summary>
    internal static (string Url, string Sha256, long Size)? FindInstaller(GitHubRelease release, string version)
    {
        var name = $"GamePingBooster-Setup-{version}.exe";
        var urlPrefix = $"https://github.com/{Repository}/releases/download/v{version}/";

        var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        if (asset?.BrowserDownloadUrl is not { } url ||
            !url.Equals(urlPrefix + name, StringComparison.Ordinal))
        {
            return null;
        }

        const string scheme = "sha256:";
        if (asset.Digest is not { } digest || !digest.StartsWith(scheme, StringComparison.Ordinal)) return null;
        var hex = digest[scheme.Length..];
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return null;

        if (asset.Size <= 0) return null;
        return (url, hex.ToLowerInvariant(), asset.Size);
    }

    /// <summary>
    /// The release's own page when it is a GitHub page of this repository, otherwise the fixed
    /// releases page. What gets opened is never an arbitrary URL out of a network response.
    /// </summary>
    internal static string SafeReleaseUrl(string? htmlUrl)
    {
        var prefix = $"https://github.com/{Repository}/releases/";
        return htmlUrl is not null && htmlUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? htmlUrl
            : ReleasesPage;
    }

    /// <summary>What this build is, as Directory.Build.props stamped it from VERSION.</summary>
    public static string? CurrentVersion() =>
        typeof(UpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// True when <paramref name="candidate"/> is a newer version than <paramref name="current"/>.
    ///
    /// The same ordering ./gpb release enforces when it cuts one, so "newer" means the same thing
    /// at both ends: x.y.z compared as numbers (0.1.10 is newer than 0.1.9), a release newer than
    /// its own pre-release (1.0.0 over 1.0.0-beta1), and two pre-releases compared as text.
    /// </summary>
    internal static bool IsNewer(string candidate, string current)
    {
        if (!TryParseVersion(candidate, out var a) || !TryParseVersion(current, out var b)) return false;

        for (var i = 0; i < 3; i++)
        {
            if (a.Numbers[i] != b.Numbers[i]) return a.Numbers[i] > b.Numbers[i];
        }
        if (a.Suffix.Length == 0) return b.Suffix.Length > 0;
        if (b.Suffix.Length == 0) return false;
        return string.CompareOrdinal(a.Suffix, b.Suffix) > 0;
    }

    private readonly record struct ParsedVersion(int[] Numbers, string Suffix);

    private static bool TryParseVersion(string? text, out ParsedVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim().TrimStart('v', 'V');
        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var suffix = dash >= 0 ? value[(dash + 1)..] : "";

        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        // No special case for 1.0.0, although AboutViewModel treats it as "no version stamped".
        // Directory.Build.props always stamps VERSION (or 0.0.0), so 1.0.0 here is a real release -
        // and treating it as a dev build would silence update notices the day 1.0.0 ships.
        version = new ParsedVersion(numbers, suffix);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}

/// <summary>
/// A newer release: its version without the leading "v", the page to open, and - when the release
/// carries a setup .exe with a digest - where to download it and what it must hash to.
/// </summary>
public sealed record AvailableUpdate(
    string Version,
    string Url,
    string? InstallerUrl = null,
    string? InstallerSha256 = null,
    long? InstallerSize = null)
{
    /// <summary>Whether it can be installed from inside the app, or only reached on its page.</summary>
    public bool CanInstall => InstallerUrl is not null && InstallerSha256 is not null;
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>"sha256:&lt;hex&gt;", computed by GitHub when the file was uploaded.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>Source-generated, because reflection-based JSON is what Native AOT trims away.</summary>
[JsonSerializable(typeof(GitHubRelease))]
internal partial class UpdateJsonContext : JsonSerializerContext;
