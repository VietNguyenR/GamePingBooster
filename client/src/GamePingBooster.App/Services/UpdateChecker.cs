using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace GamePingBooster.App.Services;

/// <summary>
/// Looks for a newer release on GitHub now and then, and says so. It never downloads or installs
/// anything: finding one adds a line to the menu, and the person clicks through to the releases
/// page if they want it.
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

        return new AvailableUpdate(latest, SafeReleaseUrl(release.HtmlUrl));
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
            if (!int.TryParse(parts[i], NumberStyles.None,
                    CultureInfo.InvariantCulture, out numbers[i]))
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

/// <summary>A newer release: its version without the leading "v", and the page to open.</summary>
public sealed record AvailableUpdate(string Version, string Url);

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
}

/// <summary>Source-generated, because reflection-based JSON is what Native AOT trims away.</summary>
[JsonSerializable(typeof(GitHubRelease))]
internal partial class UpdateJsonContext : JsonSerializerContext;
