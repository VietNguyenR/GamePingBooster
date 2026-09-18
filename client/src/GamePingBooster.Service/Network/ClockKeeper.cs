using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using GamePingBooster.Core.Net;
using Microsoft.Win32;

namespace GamePingBooster.Service.Network;

/// <summary>
/// Keeps the system clock right enough for the relays, on every Connect.
///
/// Why this exists: a relay refuses any handshake stamped more than GpbProtocol.HandshakeSkew from
/// its own clock, so a PC a few minutes out cannot connect to anything - and from the player's side
/// that looks exactly like a blocked network. Telling people to flip "Set time automatically" off
/// and on often did nothing: the Windows Time service had been disabled by some "optimiser", or
/// time.windows.com did not answer from their network. The fix that worked every time was the one
/// done by hand in Control Panel - a different time server, then Update now. This does that on every
/// Connect, whether or not the clock looks wrong, so it never becomes a support question.
///
/// On every Connect, in the background:
///   1. the Windows Time service is switched back on if it was disabled, and started;
///   2. its servers become time.cloudflare.com, time.google.com, time.windows.com, with automatic
///      synchronisation on - which IS the "Set time automatically" switch;
///   3. it is asked to synchronise, without waiting.
/// At the same time the clock is measured against the licence server. Only when it is more than
/// <see cref="CorrectAbove"/> out does Connect wait: for the time service to catch up, and if no time
/// server can be reached (UDP 123 blocked), the clock is set directly from that measurement.
///
/// The ordinary Connect therefore costs one HTTPS request, which runs while the profile loads.
///
/// What it never touches: the time zone, or anything else in the system. Only UTC moves, which is the
/// part the relays compare. A wrong time zone shows on screen and the person can see it; it has no
/// effect on connecting, and guessing a zone from an IP address is how a PC ends up an hour out.
///
/// Skipped entirely on a domain-joined PC: its time comes from the domain by policy, the policy would
/// put its own settings back, and a company machine is not ours to reconfigure.
/// </summary>
public sealed partial class ClockKeeper(Func<string?> licenceUrl, Action<string> log)
{
    /// <summary>
    /// The servers, in the order Windows is to try them. 0x9 = client mode (0x8) with the special
    /// poll interval (0x1), which is what the Control Panel dialog writes for a typed-in server.
    /// </summary>
    private const string Peers = "time.cloudflare.com,0x9 time.google.com,0x9 time.windows.com,0x9";

    /// <summary>
    /// Beyond this, Connect waits for the clock to be corrected. Half the relay's window: a clock a
    /// minute out still connects, but it is drifting towards the point where it will not.
    /// </summary>
    public static readonly TimeSpan CorrectAbove = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Close enough to call it corrected. The measurement is only good to about a second (HTTP Date
    /// has one-second resolution); the relay's window is two minutes.
    /// </summary>
    private static readonly TimeSpan Good = TimeSpan.FromSeconds(10);

    /// <summary>How long a Connect waits for the measurement. Past it, Connect goes on without one.</summary>
    private static readonly TimeSpan MeasureTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The most a Connect waits for the time service to put a wrong clock right. Past it, whatever it
    /// was doing is abandoned and the clock is set directly. A time service that hangs - locked by
    /// some other tool - must cost the player seconds, not minutes on "Correcting the system clock".
    /// </summary>
    private static readonly TimeSpan CorrectBudget = TimeSpan.FromSeconds(15);

    /// <summary>One system tool. A hung one is killed, not left behind.</summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Asked once per service start: a PC does not join or leave a domain between two Connects.</summary>
    private static readonly Lazy<bool> DomainJoined = new(IsDomainJoined);

    private Task _configuring = Task.CompletedTask;

    /// <summary>
    /// Starts switching automatic time on with Cloudflare, and measures how far out the clock is.
    /// Returns the offset - positive when this PC is ahead - or null when it could not be measured
    /// or the PC belongs to a domain. The configuration carries on in the background either way.
    /// </summary>
    public async Task<TimeSpan?> PrepareAsync(CancellationToken ct)
    {
        if (DomainJoined.Value)
        {
            log("Clock: this PC takes its time from a domain - left as it is.");
            return null;
        }

        _configuring = Task.Run(() => EnsureAutomaticTimeAsync(ct), CancellationToken.None);

        var offset = await MeasureWithinAsync(MeasureTimeout, ct).ConfigureAwait(false);
        log($"Clock: automatic time with time.cloudflare.com is on; this PC is {Describe(offset)} the licence server.");
        return offset;
    }

    /// <summary>
    /// Brings a clock that <see cref="PrepareAsync"/> found too far out back into line, and returns
    /// how far out it is afterwards (null when that could not be measured).
    /// </summary>
    public async Task<TimeSpan?> CorrectAsync(TimeSpan offset, CancellationToken ct)
    {
        log($"Clock: {Describe(offset)} the licence server - correcting it before connecting.");

        // First the time service, inside a budget. The configuration has to be in place before a
        // sync means anything; then a sync that is waited for, unlike the one PrepareAsync fired off.
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            budget.CancelAfter(CorrectBudget);
            try
            {
                await _configuring.WaitAsync(budget.Token).ConfigureAwait(false);
                await RunAsync("w32tm.exe", "/resync /force", budget.Token, logAlways: true).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), budget.Token).ConfigureAwait(false);

                if (await MeasureAsync(budget.Token).ConfigureAwait(false) is { } synced && synced.Duration() <= Good)
                {
                    log($"Clock: corrected by the time service; now {Describe(synced)} the licence server.");
                    return synced;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log($"Clock: the time service did not finish within {CorrectBudget.TotalSeconds:F0} s - setting the clock directly.");
            }
        }

        // Measured again rather than reusing an earlier number: the time service may have moved the
        // clock part of the way, and setting it from a stale offset would undo that and more.
        if (await MeasureWithinAsync(MeasureTimeout, ct).ConfigureAwait(false) is not { } now)
        {
            log("Clock: could not measure the clock again, so it was not set directly.");
            return null;
        }
        if (now.Duration() <= Good)
        {
            log($"Clock: corrected by the time service; now {Describe(now)} the licence server.");
            return now;
        }

        // No time server got through - UDP 123 blocked, most likely. Set it from the licence server's
        // clock instead, which is the reference the measurement was taken against.
        if (!SetUtcNow(DateTime.UtcNow - now))
        {
            log($"Clock: no time server answered, and setting the clock directly failed (error {Marshal.GetLastPInvokeError()}).");
            return now;
        }

        var final = await MeasureWithinAsync(MeasureTimeout, ct).ConfigureAwait(false);
        log($"Clock: no time server answered, so the clock was set directly; now {Describe(final)} the licence server.");
        return final;
    }

    // ------------------------------------------------------------------ measuring

    /// <summary>
    /// How far this PC's clock is from the licence server's, positive when this PC is ahead, or null
    /// when the server could not be reached.
    ///
    /// An estimate, not the lower bound the app uses for its warning: this one has to say how far to
    /// MOVE the clock. The server stamped Date somewhere between sending and receiving, so the midpoint
    /// is the best guess, and Date is truncated to the second, so the true server time is half a
    /// second past it on average.
    ///
    /// HTTPS first. A clock far enough out can fail certificate validation - a certificate is "not yet
    /// valid" or "expired" by the wrong clock - and then plain HTTP is used for the Date alone. That is
    /// no weaker than NTP, which is unauthenticated too; nothing else is read from it.
    /// </summary>
    private async Task<TimeSpan?> MeasureWithinAsync(TimeSpan limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        return await MeasureAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task<TimeSpan?> MeasureAsync(CancellationToken ct)
    {
        var configured = licenceUrl();
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? ServiceConfig.DefaultLicenceUrl : configured;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;

        try
        {
            return await MeasureOnceAsync(uri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException && uri.Scheme == Uri.UriSchemeHttps)
        {
            log("Clock: HTTPS refused, most likely because of the clock itself - measuring over HTTP instead.");
            var plain = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri;
            try
            {
                return await MeasureOnceAsync(plain, ct).ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                log($"Clock: could not measure over HTTP either ({inner.Message}).");
                return null;
            }
        }
        catch (Exception ex)
        {
            log($"Clock: could not reach {uri.Host} to measure the clock ({ex.Message}).");
            return null;
        }
    }

    private static async Task<TimeSpan?> MeasureOnceAsync(Uri uri, CancellationToken ct)
    {
        // No redirects followed: http:// answers with a redirect to https://, and that answer already
        // carries the Date wanted. Following it would go straight back to the certificate problem.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = HappyEyeballs.ConnectCallback,
            AllowAutoRedirect = false,
        }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        var sent = DateTimeOffset.UtcNow;
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var received = DateTimeOffset.UtcNow;

        if (response.Headers.Date is not { } date) return null;
        var midpoint = sent + (received - sent) / 2;
        return midpoint - (date + TimeSpan.FromMilliseconds(500));
    }

    private static string Describe(TimeSpan? offset) => offset is { } o
        ? $"{Math.Abs(o.TotalSeconds):F1} s {(o > TimeSpan.Zero ? "ahead of" : "behind")}"
        : "not measurable against";

    // ------------------------------------------------------------------ the time service

    /// <summary>
    /// Automatic time on, with Cloudflare first, and a sync asked for without waiting.
    ///
    /// Only what differs is changed, so a PC already set up this way - every PC after its first
    /// Connect - costs one "start" that finds the service running and one sync request. The start
    /// type is only touched when it is Disabled, the case this is for; any other is Windows' choice.
    /// </summary>
    private async Task EnsureAutomaticTimeAsync(CancellationToken ct)
    {
        try
        {
            int? start = null;
            string? type = null, servers = null;
            try
            {
                using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time");
                start = service?.GetValue("Start") as int?;
                using var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
                type = parameters?.GetValue("Type") as string;
                servers = parameters?.GetValue("NtpServer") as string;
            }
            catch (Exception ex)
            {
                log($"Clock: could not read the Windows Time settings ({ex.Message}).");
            }

            if (start == 4)
            {
                log("Clock: the Windows Time service was disabled - setting it to start automatically.");
                await RunAsync("sc.exe", "config w32time start= auto", ct, logAlways: true).ConfigureAwait(false);
            }

            // Exit 1056 is "already running", the usual answer.
            await RunAsync("sc.exe", "start w32time", ct, okExitCodes: [0, 1056]).ConfigureAwait(false);

            if (!string.Equals(type, "NTP", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(servers, Peers, StringComparison.OrdinalIgnoreCase))
            {
                log($"Clock: setting the time server to Cloudflare and turning automatic time on (was {type ?? "?"}, {servers ?? "?"}).");
                // The service takes a moment to accept configuration after a start.
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                await RunAsync("w32tm.exe", $"/config /manualpeerlist:\"{Peers}\" /syncfromflags:manual /update", ct,
                    logAlways: true).ConfigureAwait(false);
            }

            await RunAsync("w32tm.exe", "/resync /nowait", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"Clock: could not configure the Windows Time service ({ex.Message}).");
        }
    }

    /// <summary>
    /// Runs a system tool. Its output is logged when it failed, or always when asked - a Connect
    /// that finds everything already in place writes nothing.
    ///
    /// Never throws for the tool's sake. A tool that outlives <see cref="ProcessTimeout"/>, or the
    /// caller's token, is killed rather than left running in the background; a cancelled caller
    /// still gets its OperationCanceledException, so a budget above this can tell that it ran out.
    /// </summary>
    private async Task RunAsync(string file, string arguments, CancellationToken ct,
        bool logAlways = false, int[]? okExitCodes = null)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProcessTimeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (!logAlways && (okExitCodes ?? [0]).Contains(process.ExitCode)) return;

            var text = string.Join(" ", ((await output.ConfigureAwait(false)) + " " + (await error.ConfigureAwait(false)))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            log($"Clock: {file} {arguments} -> exit {process.ExitCode}{(text.Length > 0 ? ": " + text : "")}");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            log($"Clock: {file} {arguments} did not finish within {ProcessTimeout.TotalSeconds:F0} s and was stopped.");
        }
        catch (Exception ex)
        {
            log($"Clock: {file} {arguments} could not run ({ex.Message}).");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or not ours to stop. Either way there is nothing left to wait for.
        }
    }

    // ------------------------------------------------------------------ Win32

    private static bool IsDomainJoined()
    {
        var buffer = IntPtr.Zero;
        try
        {
            // NetSetupDomainName = 3. Anything else - workgroup, unjoined, unknown - is ours to keep right.
            return NetGetJoinInformation(null, out buffer, out var status) == 0 && status == 3;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    /// <summary>
    /// Sets the clock in UTC. The time zone is not involved and not changed. SetSystemTime enables
    /// the SE_SYSTEMTIME privilege itself; LocalSystem holds it.
    /// </summary>
    private static bool SetUtcNow(DateTime utc)
    {
        var time = new SystemTime
        {
            Year = (ushort)utc.Year,
            Month = (ushort)utc.Month,
            DayOfWeek = (ushort)utc.DayOfWeek,
            Day = (ushort)utc.Day,
            Hour = (ushort)utc.Hour,
            Minute = (ushort)utc.Minute,
            Second = (ushort)utc.Second,
            Milliseconds = (ushort)utc.Millisecond,
        };
        return SetSystemTime(in time);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSystemTime(in SystemTime time);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);
}

/// <summary>
/// The clock is still outside the relays' window after ClockKeeper tried everything - no time server
/// answered and setting it directly failed. Every handshake would be refused, so Connect stops here
/// and says why instead of timing out on each relay in turn.
/// </summary>
public sealed class ClockWrongException(TimeSpan offset)
    : Exception($"This PC's clock is {Math.Abs(offset.TotalSeconds):F0} s off and could not be corrected.")
{
    /// <summary>What the main window says about it. See StatusText.</summary>
    public Tunnel.StatusText Text { get; } = new("svc.clockWrong",
        $"This PC's clock is {Math.Abs(offset.TotalSeconds):F0} seconds off and could not be corrected automatically. " +
        "Set the time in Settings > Time & language, then connect again.",
        Math.Abs(offset.TotalSeconds).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
}
