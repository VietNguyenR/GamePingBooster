using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Network;
using Microsoft.Win32;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// Counts, before any sound feature is built for PUBG (owner, 2026-10-24), what the machines that play it would give
/// one: which playback devices they have, which driver and audio effects (APOs) sit on them, and whether Windows'
/// Loudness Equalization is on. Sent to the licence server's /audio-survey and read on /admin/audio-survey.
///
/// PUBG only: nothing is read or sent unless TslGame.exe is running. Read from the registry the audio service keeps
/// (HKLM\...\MMDevices\Audio\Render) - nothing is played, captured or changed, and no audio API is opened. Raw values,
/// not verdicts: the server decides what a CLSID or a loudness blob means, so a wrong reading is fixed there without
/// a client release.
///
/// Which device is the default is not in the registry as such - Windows keeps it per user, and this service is
/// LocalSystem. The active endpoint with the highest "Level:0" is marked: on the owner's machine that was the default
/// for every role, checked against IMMDeviceEnumerator on 2026-10-24. Every active endpoint is sent, so the count
/// does not hang on that guess.
///
/// The user-renamable label ("Speakers", "Tai nghe của ...") is not read. Once per service run, and again only when
/// what would be sent changed (a headset plugged in). The server keeps one row per device, overwritten. Under the
/// "Send connection quality" switch and signed by this device, like the other reports.
/// </summary>
internal sealed class AudioSurvey : IDisposable
{
    public const string Domain = "gpb-audio-survey-v1\n";
    public const string GameId = "pubg";
    private const string GameProcess = "TslGame.exe";

    private const string RenderPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

    // Endpoint properties (Properties subkey).
    private const string AdapterName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string EnumeratorName = "{a45c254e-df1c-4efd-8020-67d146a850e0},24";
    private const string DriverInf = "{83da6326-97a6-4088-9453-a1923f573b29},3";
    private const string FormFactor = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0";
    private const string DisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    // Windows' own enhancements (FxProperties subkey): Loudness Equalization on/off and its release time.
    private const string Loudness = "{fc52a749-4be9-4510-896e-966ba6525980},3";
    private const string LoudnessRelease = "{9c00eeed-edce-4cd8-ae08-cb05e8ef57a0},3";

    private const int MaxEndpoints = 16;
    private const int MaxFxStrings = 32;
    private const int MaxText = 200;

    private static readonly TimeSpan LookEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const int TriesPerSurvey = 5;

    private static readonly string? AppVersion = typeof(AudioSurvey).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private readonly Func<string?> _licenceUrl;
    private readonly Func<byte[]?> _token;
    private readonly ECDsa _deviceKey;
    private readonly Func<bool> _allowed;
    private readonly Action<string> _log;

    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private readonly Task _loop;

    public AudioSurvey(Func<string?> licenceUrl, Func<byte[]?> token, ECDsa deviceKey, Func<bool> allowed, Action<string> log)
    {
        _licenceUrl = licenceUrl;
        _token = token;
        _deviceKey = deviceKey;
        _allowed = allowed;
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback }, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string? sent = null;
        string? trying = null;
        var tries = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LookEvery, ct).ConfigureAwait(false);
                if (!_allowed() || GameProcessWatcher.FindRunning([GameProcess]) is null) continue;

                var endpoints = ReadEndpoints();
                if (endpoints.Count == 0) continue;
                var survey = SurveyText(endpoints);
                if (survey == sent) continue;
                if (survey != trying)
                {
                    trying = survey;
                    tries = 0;
                }
                if (tries >= TriesPerSurvey) continue;
                tries++;

                if (await SendAsync(endpoints, ct).ConfigureAwait(false)) sent = survey;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"Audio survey: could not read or send ({ex.Message}).");
            }
        }
    }

    internal sealed record Endpoint(
        bool Default, string? Adapter, string? Enumerator, string? Driver, int? FormFactor, int? SysFxDisabled,
        int FxCount, IReadOnlyList<KeyValuePair<string, string>> Fx, string? Loudness, string? LoudnessRelease);

    /// <summary>Every active playback endpoint, as the audio service's registry describes it.</summary>
    internal static List<Endpoint> ReadEndpoints()
    {
        var found = new List<(long Level, Endpoint Endpoint)>();
        using var render = Registry.LocalMachine.OpenSubKey(RenderPath);
        if (render is null) return [];

        foreach (var name in render.GetSubKeyNames())
        {
            try
            {
                using var key = render.OpenSubKey(name);
                // DEVICE_STATE_ACTIVE; the high bits of unplugged and disabled ones are other flags.
                if (key?.GetValue("DeviceState") is not int state || state != 1) continue;
                // A QWORD; read either width in case another build writes a DWORD.
                var level = key.GetValue("Level:0") switch { long q => q, int d => d, _ => -1L };

                using var props = key.OpenSubKey("Properties");
                using var fx = key.OpenSubKey("FxProperties");

                var strings = new List<KeyValuePair<string, string>>();
                if (fx is not null)
                {
                    foreach (var valueName in fx.GetValueNames().Order(StringComparer.Ordinal))
                    {
                        if (strings.Count >= MaxFxStrings) break;
                        var text = fx.GetValue(valueName) switch
                        {
                            string s => s,
                            string[] many => string.Join(";", many),
                            _ => null,
                        };
                        if (text is not null) strings.Add(new(valueName, Clip(text)!));
                    }
                }

                found.Add((level, new Endpoint(
                    Default: false,
                    Adapter: Clip(props?.GetValue(AdapterName) as string),
                    Enumerator: Clip(props?.GetValue(EnumeratorName) as string),
                    Driver: Clip(props?.GetValue(DriverInf) as string),
                    FormFactor: props?.GetValue(FormFactor) as int?,
                    // Windows has kept this switch in either place over the years.
                    SysFxDisabled: props?.GetValue(DisableSysFx) as int? ?? fx?.GetValue(DisableSysFx) as int?,
                    FxCount: fx?.ValueCount ?? 0,
                    Fx: strings,
                    Loudness: Hex(fx?.GetValue(Loudness)),
                    LoudnessRelease: Hex(fx?.GetValue(LoudnessRelease)))));
            }
            catch (Exception)
            {
                // One endpoint the service cannot read is left out; the rest still count.
            }
        }

        var top = found.Count == 0 ? -1 : found.Max(f => f.Level);
        var marked = false;
        return found
            .OrderByDescending(f => f.Level)
            .Take(MaxEndpoints)
            .Select(f =>
            {
                if (marked || f.Level != top || top < 0) return f.Endpoint;
                marked = true;
                return f.Endpoint with { Default = true };
            })
            .ToList();
    }

    private static string? Clip(string? text) => text is null ? null : text.Length <= MaxText ? text : text[..MaxText];

    private static string? Hex(object? value) => value is byte[] { Length: > 0 and <= 64 } bytes ? Convert.ToHexStringLower(bytes) : null;

    /// <summary>What the survey says, without the time: sent again only when this changes.</summary>
    private static string SurveyText(List<Endpoint> endpoints) => Encoding.UTF8.GetString(Body(endpoints, null));

    private async Task<bool> SendAsync(List<Endpoint> endpoints, CancellationToken ct)
    {
        var url = _licenceUrl();
        var token = _token();
        if (string.IsNullOrWhiteSpace(url) || token is null) return false;

        var body = Body(endpoints, DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/audio-survey")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Gpb-Licence", Convert.ToHexStringLower(token));
        request.Headers.Add("X-Gpb-Signature", DiscoveryReport.Sign(_deviceKey, body, Domain));

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                _log($"Audio survey: sent ({endpoints.Count} playback device(s)).");
                return true;
            }
            _log($"Audio survey: the licence server answered {status}.");
            // Refused for what it is, not for now: asking again with the same body changes nothing.
            return status is 400 or 404 or 413;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log($"Audio survey: could not reach the licence server ({ex.Message}).");
            return false;
        }
    }

    /// <summary>What /audio-survey reads - see app/lib/audio-survey.server.ts in the web service.</summary>
    internal static byte[] Body(IReadOnlyList<Endpoint> endpoints, DateTimeOffset? sentAt)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("game", GameId);
            if (sentAt is { } at) json.WriteString("sentAt", at.UtcDateTime.ToString("O"));
            if (AppVersion is not null) json.WriteString("appVersion", AppVersion);
            json.WriteString("os", OsVersion());
            json.WriteStartArray("endpoints");
            foreach (var e in endpoints)
            {
                json.WriteStartObject();
                json.WriteBoolean("default", e.Default);
                WriteOrNull(json, "adapter", e.Adapter);
                WriteOrNull(json, "enumerator", e.Enumerator);
                WriteOrNull(json, "driver", e.Driver);
                if (e.FormFactor is { } form) json.WriteNumber("formFactor", form);
                else json.WriteNull("formFactor");
                if (e.SysFxDisabled is { } off) json.WriteNumber("sysFxDisabled", off);
                else json.WriteNull("sysFxDisabled");
                json.WriteNumber("fxCount", e.FxCount);
                json.WriteStartObject("fx");
                foreach (var (name, value) in e.Fx) json.WriteString(name, value);
                json.WriteEndObject();
                WriteOrNull(json, "loudness", e.Loudness);
                WriteOrNull(json, "loudnessRelease", e.LoudnessRelease);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteOrNull(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null) json.WriteNull(name);
        else json.WriteString(name, value);
    }

    /// <summary>"10.0.26200.6901": the build decides what Windows allows an APO, so it is counted beside the device.</summary>
    private static string OsVersion()
    {
        var v = Environment.OSVersion.Version;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("UBR") is int ubr) return $"{v.Major}.{v.Minor}.{v.Build}.{ubr}";
        }
        catch (Exception)
        {
        }
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _http.Dispose();
        _cts.Dispose();
    }
}
