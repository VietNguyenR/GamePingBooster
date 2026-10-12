using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Updates;

namespace GamePingBooster.Service.Update;

/// <summary>
/// Installs new releases by itself: no window, no button, no UAC prompt (0.5.0).
///
/// Why the service and not the app. Until 0.4.x the app found a release, opened a window at every start,
/// and the player pressed Update and then Yes on the Windows prompt - for every release, and releases come
/// several times a day when something is broken (players asked why it "updates all the time", 2026-10-13).
/// The prompt was the worst of it, and it exists only because the app is unprivileged. This process already
/// runs as LocalSystem, so it can run setup silently with nobody asked anything.
///
/// The flow:
///   1. Look for a release a little after start, then hourly, and at once when the app asks (check-update,
///      sent when the licence server refuses this version as too old). Same feed as the app: ReleaseFeed.
///   2. Download it in the background, and check it twice: GitHub's SHA-256 and our Authenticode signature.
///   3. Wait for a moment nobody will notice: not connected, not connecting, no game running, and quiet for
///      <see cref="QuietBeforeInstall"/>. Never during a match - a release waits for the player, not the
///      other way round.
///   4. Run setup /VERYSILENT through cmd, which starts the service again if setup gave up (see Install).
///      Setup closes the app, stops this service, copies, and starts the new service.
///   5. The new service reads <see cref="RecordPath"/> as it starts (FinishPreviousAttempt): it tells
///      whether the update took, and starts the app again in the session it was open in.
///
/// A release that will not install - an unknown signature, or setup failing <see cref="MaxAttempts"/> times
/// - is "refused" and never tried again by the service; the app then offers its own window for it, the way
///   every update went before. Nothing here can leave a machine without a working version.
///
/// Security: this runs a downloaded program as SYSTEM, so nothing about WHAT runs comes from the pipe. The
/// app can only ask for a check; the release, the file and the moment are all decided here, and the file
/// lives in a folder only SYSTEM and Administrators can write (see PrivateFolder).
/// </summary>
internal sealed class AutoUpdater : IAsyncDisposable
{
    /// <summary>How often GitHub is asked. Its redirect costs nothing against the API limit; see ReleaseFeed.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    /// <summary>The first check, a moment after start: the profile loads and the app connects first.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);

    /// <summary>How often the moment to install is looked for while a download waits.</summary>
    private static readonly TimeSpan GateTick = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Disconnected for at least this long first. Changing game is a disconnect and a connect a second apart,
    /// and an update must never land in that gap.
    /// </summary>
    public static readonly TimeSpan QuietBeforeInstall = TimeSpan.FromSeconds(30);

    /// <summary>A release whose setup fails this many times is left to the app's own window.</summary>
    public const int MaxAttempts = 2;

    /// <summary>How long "updated to x.y.z" is shown in the app after an update.</summary>
    private static readonly TimeSpan UpdatedToShownFor = TimeSpan.FromMinutes(10);

    /// <summary>The app is started again only if the update finished within this time - otherwise the player has moved on.</summary>
    private static readonly TimeSpan RelaunchWithin = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an update-now (the updater window is open) stays in force. Long enough for a check and a 20 MB
    /// download on a slow line; after it, an update that was asked for and never got there goes back to waiting
    /// for a quiet moment like any other.
    /// </summary>
    private static readonly TimeSpan AttendedFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// With the app open, an update about to install is first offered to it ("ready"), so it can open the
    /// updater window and the player sees every step. An app that has not answered by then - hidden behind
    /// something, or hung - gets the silent install, with the app started again afterwards.
    /// </summary>
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// After an update the updater window watched, the window starts the app itself. The service starts it only
    /// when no app has connected after this long - the window was closed or died on the way.
    /// </summary>
    private static readonly TimeSpan RelaunchFallbackAfter = TimeSpan.FromSeconds(45);

    private const string ServiceName = "GamePingBooster";
    private const string UiExe = "GamePingBooster.exe";

    /// <summary>State that must outlive this process: what was being installed, how many tries, which releases are refused.</summary>
    internal static string Folder => Path.Combine(ServiceConfig.DefaultDirectory, "update");
    internal static string RecordPath => Path.Combine(Folder, "update-state.json");
    private static string DownloadFolder => Path.Combine(Folder, "download");
    private static string CachePath => Path.Combine(Folder, "update-check.json");

    private readonly Func<TunnelState> _tunnelState;
    private readonly Func<bool> _gameRunning;
    private readonly Func<uint?> _uiSession;
    private readonly Action<string> _log;
    private readonly string? _current = CurrentVersion();
    private readonly SemaphoreSlim _kick = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;

    private Phase _phase = Phase.None;
    private string? _version;
    private string? _installerPath;
    private string? _installerSha256;
    private DateTimeOffset _lastBusyAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _installNotBefore = DateTimeOffset.MinValue;
    private string? _lastWhyNot;
    private string? _updatedTo;
    private DateTimeOffset _updatedToUntil;
    private uint? _relaunchSession;
    private bool _relaunchAttended;
    private bool _checkRequested;
    private DateTimeOffset _attendedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _readySince;

    private enum Phase { None, Downloading, Waiting, Installing, Refused }

    public AutoUpdater(bool enabled, Func<TunnelState> tunnelState, Func<bool> gameRunning, Func<uint?> uiSession, Action<string> log)
    {
        _notInstalled = WhyNotTheInstalledCopy();
        Enabled = enabled && _notInstalled is null;
        _switchedOff = !enabled;
        _tunnelState = tunnelState;
        _gameRunning = gameRunning;
        _uiSession = uiSession;
        _log = log;
        FinishPreviousAttempt();
    }

    /// <summary>
    /// False when config.json says autoUpdate: false, or when this is not the copy setup installed - a developer's
    /// `./gpb dev` or `./gpb publish`, or the test rig. Those carry the release's version too, and would otherwise
    /// install the published release over the developer's machine. The app then offers its own window.
    /// </summary>
    public bool Enabled { get; }

    private readonly string? _notInstalled;
    private readonly bool _switchedOff;

    /// <summary>Inno Setup's uninstall entry: the AppId in GamePingBooster.iss plus "_is1".</summary>
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6E7A4C21-8D3F-4B62-9E15-2C4A7F9D0B83}_is1";

    /// <summary>Null when this process runs from the folder setup installed to (its uninstall entry says where); otherwise why not.</summary>
    private static string? WhyNotTheInstalledCopy()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var entry = hklm.OpenSubKey(UninstallKey);
            if (entry?.GetValue("InstallLocation") is not string location || location.Length == 0)
            {
                return "setup has not installed the app on this PC";
            }
            var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            var running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            return string.Equals(installed, running, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"this copy runs from {running}, not from where setup installed the app ({installed})";
        }
        catch (Exception ex)
        {
            return $"the installation could not be read ({ex.Message})";
        }
    }

    /// <summary>Setup has been started; connecting now would be cut off by it within seconds.</summary>
    public bool Installing
    {
        get { lock (_gate) return _phase == Phase.Installing; }
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>The app asked for a check now (check-update). Only a check: the rest is decided here.</summary>
    public void CheckNow()
    {
        lock (_gate) _checkRequested = true;
        _kick.Release();
    }

    /// <summary>
    /// update-now: the updater window is open, as the app starts or because this service said "ready". Check now,
    /// and install a release at once rather than after the quiet period - the player is watching it happen - but
    /// still never while boosting or with a game running. Every step goes to <see cref="UpdateProgress"/>, and
    /// "nothing to install" is written too, so the window never waits on silence.
    ///
    /// Like check-update this chooses nothing: which release, and whether it may run, are still decided here.
    /// </summary>
    public void UpdateNow()
    {
        if (!Enabled)
        {
            Report(UpdateProgress.None, reason: UpdateProgress.ReasonOff);
            return;
        }
        lock (_gate)
        {
            _attendedUntil = DateTimeOffset.UtcNow + AttendedFor;
            _checkRequested = true;
            _readySince = null;
        }
        _kick.Release();
    }

    private bool Attended
    {
        get { lock (_gate) return DateTimeOffset.UtcNow < _attendedUntil; }
    }

    private void EndAttended()
    {
        lock (_gate) _attendedUntil = DateTimeOffset.MinValue;
    }

    /// <summary>Writes where the update is for the updater window. Never fails the update: the window is only a view.</summary>
    private void Report(string phase, string? version = null, long? done = null, long? total = null, string? reason = null)
    {
        try
        {
            if (!PrivateFolder.Ensure(Folder, out _)) return;
            if (!PrivateFolder.Ensure(UpdateProgress.FolderPath, out _, usersRead: true)) return;
            AtomicFile.WriteAllText(UpdateProgress.FilePath, UpdateProgress.Format(phase, version, _current, done, total, reason));
        }
        catch (Exception)
        {
            // A window that shows a stale line is the whole cost.
        }
    }

    /// <summary>Download progress for <see cref="Report"/>, at most four lines a second.</summary>
    private sealed class DownloadProgress(AutoUpdater owner, string version, long? total) : IProgress<long>
    {
        private long _lastAt;

        public void Report(long received)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastAt) < 250 && received != total) return;
            Interlocked.Exchange(ref _lastAt, now);
            owner.Report(UpdateProgress.Downloading, version, received, total);
        }
    }

    /// <summary>
    /// Every status the engine raises. "Quiet for 30 s" counts from the last one that was not idle, or from the
    /// first idle one after it - the moment boosting stopped. Idle statuses after that move nothing: the engine
    /// raises some for other reasons, and they must not hold an update back forever.
    /// </summary>
    public void NoteTunnel(TunnelState state)
    {
        var idle = state is TunnelState.Disconnected or TunnelState.Faulted;
        lock (_gate)
        {
            if (!idle || !_wasIdle) _lastBusyAt = DateTimeOffset.UtcNow;
            _wasIdle = idle;
        }
    }

    private bool _wasIdle = true;

    /// <summary>Puts where the update is into a status before it goes to the app. See StatusMessage.UpdateState.</summary>
    public void Stamp(StatusMessage status)
    {
        lock (_gate)
        {
            status.AutoUpdate = Enabled;
            status.UpdateState = _phase switch
            {
                Phase.Downloading => "downloading",
                Phase.Waiting when _readySince is not null => "ready",
                Phase.Waiting => "waiting",
                Phase.Installing => "installing",
                Phase.Refused => "refused",
                _ => null,
            };
            status.UpdateVersion = _phase == Phase.None ? null : _version;
            status.UpdatedTo = _updatedTo is not null && DateTimeOffset.UtcNow < _updatedToUntil ? _updatedTo : null;
        }
    }

    // ------------------------------------------------------------------ the loop

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            // The app was closed by the setup that put this version here: start it again once the pipe is up -
            // unless the updater window watched the update, which starts it by itself (see RelaunchUnlessAppComes).
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (_relaunchSession is { } session)
            {
                if (_relaunchAttended) _ = RelaunchUnlessAppComesAsync(session, ct);
                else Relaunch(session);
            }

            if (!Enabled)
            {
                _log(_switchedOff
                    ? "Automatic updates are switched off in config.json (autoUpdate: false)."
                    : $"Automatic updates are off: {_notInstalled}.");
                return;
            }

            var nextCheck = DateTimeOffset.UtcNow + FirstCheckDelay;
            while (!ct.IsCancellationRequested)
            {
                bool asked;
                lock (_gate)
                {
                    asked = _checkRequested;
                    _checkRequested = false;
                }
                if (asked || DateTimeOffset.UtcNow >= nextCheck)
                {
                    nextCheck = DateTimeOffset.UtcNow + CheckInterval;
                    await CheckAndDownloadAsync(ct).ConfigureAwait(false);
                }

                bool waiting, offered;
                lock (_gate) waiting = _phase == Phase.Waiting;
                if (waiting) TryInstall(ct);
                lock (_gate) offered = _readySince is not null;

                // While the app has been offered the update, look again every second or two: its answer (update-now)
                // kicks the loop anyway, and ReadyWait must end on time when no answer comes.
                await _kick.WaitAsync(offered ? TimeSpan.FromSeconds(2) : GateTick, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down - setup stopping the service is the usual way here.
        }
        catch (Exception ex)
        {
            // Never takes the service down with it: the tunnel matters more than an update.
            _log($"Automatic update stopped: {ex}");
        }
    }

    private async Task CheckAndDownloadAsync(CancellationToken ct)
    {
        // A check asked for while setup runs (or a download is under way) changes nothing it is doing. A download
        // under way already reports its progress, which is what an updater window opened now wants to see.
        var attended = Attended;
        lock (_gate)
        {
            if (_phase is Phase.Installing or Phase.Downloading) return;
            // Downloaded and checked already: the window goes straight on to the install (TryInstall, next).
            if (attended && _phase == Phase.Waiting && _installerPath is not null && File.Exists(_installerPath)) return;
        }

        if (attended) Report(UpdateProgress.Checking);

        // "Nothing to install now" for the updater window, which then opens the app. Only when one is watching.
        void NothingNow(string reason)
        {
            if (!attended) return;
            Report(UpdateProgress.None, reason: reason);
            EndAttended();
        }

        AvailableUpdate? update;
        try
        {
            if (!PrivateFolder.Ensure(Folder, out var problem))
            {
                _log($"Automatic update: {problem}");
                NothingNow(UpdateProgress.ReasonUnavailable);
                return;
            }
            update = await ReleaseFeed.CheckAsync(_current, CachePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log($"Automatic update: could not check for a new release ({ex.Message}).");
            NothingNow(UpdateProgress.ReasonUnavailable);
            return;
        }

        if (update is null)
        {
            // Nothing newer (or a release was withdrawn): forget whatever was waiting.
            lock (_gate)
            {
                if (_phase is Phase.Waiting or Phase.Refused) _phase = Phase.None;
                _readySince = null;
            }
            NothingNow(UpdateProgress.ReasonLatest);
            return;
        }

        var record = UpdateRecord.Read();
        lock (_gate)
        {
            if (record.Refused.Contains(update.Version))
            {
                _phase = Phase.Refused;
                _version = update.Version;
            }
            else if (_phase == Phase.Waiting && ReleaseFeed.SameVersion(_version, update.Version) &&
                     _installerPath is not null && File.Exists(_installerPath))
            {
                return; // Already downloaded and checked.
            }
        }
        if (record.Refused.Contains(update.Version))
        {
            NothingNow(UpdateProgress.ReasonRefused);
            return;
        }

        if (!update.CanInstall)
        {
            // GitHub's API refused (rate limit) or the release has no setup yet. The next hourly check tries again.
            _log($"Automatic update: v{update.Version} is out but its installer could not be looked up yet.");
            NothingNow(UpdateProgress.ReasonUnavailable);
            return;
        }

        lock (_gate)
        {
            _phase = Phase.Downloading;
            _version = update.Version;
        }
        _log($"Automatic update: downloading v{update.Version} ({update.InstallerSize / 1024 ?? 0} KB).");
        Report(UpdateProgress.Downloading, update.Version, 0, update.InstallerSize);

        try
        {
            if (!PrivateFolder.Recreate(DownloadFolder, out var problem))
            {
                _log($"Automatic update: {problem}");
                lock (_gate) _phase = Phase.None;
                if (attended)
                {
                    Report(UpdateProgress.Failed, update.Version, reason: UpdateProgress.ReasonDownload);
                    EndAttended();
                }
                return;
            }
            var progress = new DownloadProgress(this, update.Version, update.InstallerSize);
            var path = await InstallerDownload.DownloadAsync(update, DownloadFolder, _current, progress, ct).ConfigureAwait(false);

            Report(UpdateProgress.Verifying, update.Version);
            if (Authenticode.Check(path) is { } refusal)
            {
                _log($"Automatic update: v{update.Version} is refused - {refusal}. The app will offer it in its own window.");
                TryDelete(path);
                Refuse(update.Version);
                if (Attended)
                {
                    Report(UpdateProgress.Failed, update.Version, reason: UpdateProgress.ReasonSignature);
                    EndAttended();
                }
                return;
            }

            lock (_gate)
            {
                _installerPath = path;
                _installerSha256 = update.InstallerSha256;
                _phase = Phase.Waiting;
                _lastWhyNot = null;
            }
            _log($"Automatic update: v{update.Version} downloaded and verified; it installs once nothing is connected or playing.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log($"Automatic update: downloading v{update.Version} failed ({ex.Message}). Tried again at the next check.");
            lock (_gate) _phase = Phase.None;
            if (Attended)
            {
                Report(UpdateProgress.Failed, update.Version, reason: UpdateProgress.ReasonDownload);
                EndAttended();
            }
        }
    }

    /// <summary>Why now is not the moment, or null when it is.</summary>
    private string? WhyNotNow()
    {
        var now = DateTimeOffset.UtcNow;
        if (_tunnelState() is not (TunnelState.Disconnected or TunnelState.Faulted)) return "boosting is on";
        lock (_gate)
        {
            if (now - _lastBusyAt < QuietBeforeInstall) return "boosting was on a moment ago";
            if (now < _installNotBefore) return "the last attempt failed - waiting before the next";
        }
        if (_gameRunning()) return "a game is running";
        return null;
    }

    /// <summary>
    /// The same, with the updater window open: the player is watching, so the quiet period is not needed. Boosting
    /// and a running game still stop it - the window then says so and opens the app, and the update waits.
    /// </summary>
    private (string Why, string Reason)? WhyNotNowAttended()
    {
        if (_tunnelState() is not (TunnelState.Disconnected or TunnelState.Faulted)) return ("boosting is on", UpdateProgress.ReasonBusy);
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _installNotBefore) return ("the last attempt failed - waiting before the next", UpdateProgress.ReasonLater);
        }
        if (_gameRunning()) return ("a game is running", UpdateProgress.ReasonGame);
        return null;
    }

    private void TryInstall(CancellationToken ct)
    {
        var attended = Attended;
        var why = attended ? WhyNotNowAttended()?.Why : WhyNotNow();
        if (why is not null)
        {
            bool changed;
            lock (_gate)
            {
                changed = why != _lastWhyNot;
                _lastWhyNot = why;
                _readySince = null;
            }
            if (changed) _log($"Automatic update: v{_version} waits - {why}.");
            if (attended)
            {
                Report(UpdateProgress.None, _version, reason: WhyNotNowAttended()?.Reason ?? UpdateProgress.ReasonBusy);
                EndAttended();
            }
            return;
        }

        // The app is open: offer it the update first, so it opens the updater window and the player sees each
        // step instead of the app vanishing. Its update-now makes this attended; no answer within ReadyWait and
        // the update goes ahead silently, as it does with no app open.
        if (!attended && _uiSession() is not null)
        {
            lock (_gate)
            {
                if (_readySince is null)
                {
                    _readySince = DateTimeOffset.UtcNow;
                    _log($"Automatic update: v{_version} is ready to install; asking the app to show the updater window.");
                    return;
                }
                if (DateTimeOffset.UtcNow - _readySince < ReadyWait) return;
            }
            _log($"Automatic update: the app did not open the updater window within {ReadyWait.TotalSeconds:F0} s - installing without it.");
        }
        lock (_gate) _readySince = null;

        string version, path;
        lock (_gate)
        {
            if (_installerPath is null || _version is null) return;
            version = _version;
            path = _installerPath;
            // Installing BEFORE the last look at the tunnel: from here a connect is refused (PipeServer), so one
            // pressed in this instant either landed before - and is seen below - or is turned away.
            _phase = Phase.Installing;
        }
        if (_tunnelState() is not (TunnelState.Disconnected or TunnelState.Faulted))
        {
            lock (_gate) _phase = Phase.Waiting;
            if (attended)
            {
                Report(UpdateProgress.None, version, reason: UpdateProgress.ReasonBusy);
                EndAttended();
            }
            return;
        }

        // Checked again right before it runs, not trusted from the download an hour ago.
        string? problem = null;
        try
        {
            if (!File.Exists(path)) problem = "the downloaded installer is gone";
            else if (!string.Equals(InstallerDownload.HashFile(path), _installerSha256, StringComparison.OrdinalIgnoreCase)) problem = "the downloaded installer changed";
            else problem = Authenticode.Check(path);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }
        if (problem is not null)
        {
            _log($"Automatic update: not installing v{version} - {problem}. Downloaded again at the next check.");
            lock (_gate)
            {
                _phase = Phase.None;
                _installerPath = null;
            }
            if (attended)
            {
                Report(UpdateProgress.Failed, version, reason: UpdateProgress.ReasonSignature);
                EndAttended();
            }
            return;
        }

        var record = UpdateRecord.Read();
        record.Attempts = ReleaseFeed.SameVersion(record.Version, version) ? record.Attempts + 1 : 1;
        record.Version = version;
        record.InFlight = version;
        record.From = _current;
        record.Session = _uiSession();
        record.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The updater window is watching: it starts the app itself once the new service is up.
        record.Attended = attended;
        record.Write();
        Report(UpdateProgress.Starting, version);
        EndAttended();

        _log($"Automatic update: installing v{version} over v{_current} (attempt {record.Attempts} of {MaxAttempts})" +
             (record.Session is { } s ? $"; the app is open in session {s} and is started again afterwards." : "."));

        Process? setup;
        try
        {
            setup = Process.Start(SetupCommand(path));
        }
        catch (Exception ex)
        {
            setup = null;
            _log($"Automatic update: setup could not be started ({ex.Message}).");
        }

        if (setup is null)
        {
            Report(UpdateProgress.Failed, version, reason: UpdateProgress.ReasonSetup);
            Failed(version, record);
            return;
        }

        // Setup stops this service, normally within seconds, and the new one takes over in FinishPreviousAttempt.
        // If this process is still here when setup has ended, it gave up before stopping anything.
        _ = Task.Run(async () =>
        {
            using (setup)
            {
                try { await setup.WaitForExitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            _log($"Automatic update: setup for v{version} ended without replacing this service. See {SetupLogPath}.");
            var latest = UpdateRecord.Read();
            Report(UpdateProgress.Failed, version, reason: UpdateProgress.ReasonSetup);
            Failed(version, latest);
            if (latest.Session is { } session)
            {
                if (latest.Attended) await RelaunchUnlessAppComesAsync(session, CancellationToken.None).ConfigureAwait(false);
                else Relaunch(session);
            }
        }, CancellationToken.None);
    }

    private static string SetupLogPath => Path.Combine(DownloadFolder, "setup.log");

    /// <summary>
    /// Setup, silently, and then "sc start" whatever happened. On success setup has already started the new
    /// service and the second start is a harmless "already running". On failure - setup refuses part-way, after
    /// it has stopped this service - it is the only thing that brings the old one back: nothing else would,
    /// because a stop is not a crash and the SCM's restart-on-failure does not see it.
    ///
    /// cmd /s /c "..." takes everything between the first and last quote literally; every path in it is ours.
    /// </summary>
    private static ProcessStartInfo SetupCommand(string installer)
    {
        var system = Environment.SystemDirectory;
        var sc = Path.Combine(system, "sc.exe");
        // /VERYSILENT: no window at all. /AUTOUPDATE=1: ours - setup leaves starting the app to the new service.
        var setup = $"\"{installer}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /AUTOUPDATE=1 /LOG=\"{SetupLogPath}\"";
        return new ProcessStartInfo(Path.Combine(system, "cmd.exe"))
        {
            Arguments = $"/d /s /c \"{setup} & \"{sc}\" start {ServiceName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = system,
        };
    }

    private void Failed(string version, UpdateRecord record)
    {
        record.InFlight = null;
        if (record.Attempts >= MaxAttempts && !record.Refused.Contains(version)) record.Refused.Add(version);
        record.Write();

        lock (_gate)
        {
            _installNotBefore = DateTimeOffset.UtcNow + CheckInterval;
            _phase = record.Refused.Contains(version) ? Phase.Refused
                : _installerPath is null ? Phase.None // After a restart: downloaded again at the next check.
                : Phase.Waiting;
        }
        if (record.Refused.Contains(version))
        {
            _log($"Automatic update: v{version} failed {record.Attempts} times and is left to the app's own update window.");
        }
    }

    private void Refuse(string version)
    {
        var record = UpdateRecord.Read();
        if (!record.Refused.Contains(version)) record.Refused.Add(version);
        record.Write();
        lock (_gate)
        {
            _phase = Phase.Refused;
            _version = version;
        }
    }

    // ------------------------------------------------------------------ after an update

    /// <summary>
    /// Runs as the service starts: did the update the last service started take? Either way the app it closed is
    /// started again (from the loop, once the pipe is listening), and the attempt is closed.
    /// </summary>
    private void FinishPreviousAttempt()
    {
        UpdateRecord record;
        try
        {
            record = UpdateRecord.Read();
        }
        catch (Exception)
        {
            return;
        }
        if (record.InFlight is not { } target) return;

        // Something newer than what setup was started for is running: installed by hand since, which also opened
        // the app. Not a failure of the old attempt, and nothing to start again.
        if (_current is not null && ReleaseFeed.IsNewer(_current, target))
        {
            _log($"Automatic update: v{target} was being installed, but v{_current} is running now - that attempt is closed.");
            record.InFlight = null;
            record.Attempts = 0;
            record.Write();
            return;
        }

        var startedAt = DateTimeOffset.FromUnixTimeSeconds(record.StartedAt);
        if (record.Session is { } session && DateTimeOffset.UtcNow - startedAt < RelaunchWithin) _relaunchSession = session;
        _relaunchAttended = record.Attended;

        if (ReleaseFeed.SameVersion(_current, target))
        {
            _log($"Automatic update: now running v{_current} (was v{record.From}), " +
                 $"{(DateTimeOffset.UtcNow - startedAt).TotalSeconds:F0} s after setup was started.");
            record.InFlight = null;
            record.Attempts = 0;
            record.Write();
            lock (_gate)
            {
                _updatedTo = _current;
                _updatedToUntil = DateTimeOffset.UtcNow + UpdatedToShownFor;
            }
            // The installer is done with; the log stays until the next download.
            try
            {
                foreach (var exe in Directory.EnumerateFiles(DownloadFolder, "*.exe")) TryDelete(exe);
            }
            catch (Exception) { }
            return;
        }

        _log($"Automatic update: v{target} did not install (still v{_current}, attempt {record.Attempts} of {MaxAttempts}). " +
             $"See {SetupLogPath}.");
        // The old service is back (AutoUpdater.SetupCommand's sc start): tell a watching updater window.
        Report(UpdateProgress.Failed, target, reason: UpdateProgress.ReasonSetup);
        Failed(target, record);
    }

    /// <summary>
    /// After an update the updater window watched: the window starts the app itself. Started here only if no app
    /// has connected by <see cref="RelaunchFallbackAfter"/> - the window was closed, or died on the way.
    /// </summary>
    private async Task RelaunchUnlessAppComesAsync(uint session, CancellationToken ct)
    {
        try
        {
            var until = DateTimeOffset.UtcNow + RelaunchFallbackAfter;
            while (DateTimeOffset.UtcNow < until)
            {
                if (_uiSession() is not null) return;
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            _log("Automatic update: the updater window did not start the app - starting it from here.");
            Relaunch(session);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Relaunch(uint session)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, UiExe);
        if (!File.Exists(exe)) return;
        var problem = UserSession.Start(session, exe);
        _log(problem is null
            ? $"Automatic update: started the app again in session {session}."
            : $"Automatic update: could not start the app again in session {session} ({problem}).");
    }

    // ------------------------------------------------------------------ helpers

    internal static string? CurrentVersion() =>
        typeof(AutoUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _kick.Dispose();
    }
}

/// <summary>
/// A folder only SYSTEM and Administrators can write. %ProgramData% lets every user create files and folders,
/// and owns what they create - so a folder of the right name that already exists is NOT trusted: unless it is
/// ours (owned by SYSTEM or Administrators, inheritance cut), it is removed and made again. An installer that is
/// about to run as SYSTEM can sit nowhere else.
///
/// <c>usersRead</c>: Users may also read it - update\status, where the updater window (running as the user) reads
/// how the update is going. Still written by SYSTEM and Administrators only.
/// </summary>
internal static class PrivateFolder
{
    public static bool Ensure(string path, out string? problem, bool usersRead = false)
    {
        problem = null;
        try
        {
            var info = new DirectoryInfo(path);
            if (info.Exists)
            {
                var security = info.GetAccessControl();
                var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                var ours = owner is not null &&
                           (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) &&
                           security.AreAccessRulesProtected;
                if (ours && (info.Attributes & FileAttributes.ReparsePoint) == 0) return true;
                info.Delete(recursive: true);
            }
            info.Create(Security(usersRead));
            return true;
        }
        catch (Exception ex)
        {
            problem = $"could not prepare {path} ({ex.Message})";
            return false;
        }
    }

    /// <summary>Emptied and made again: a download never starts beside anything left from before.</summary>
    public static bool Recreate(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            new DirectoryInfo(path).Create(Security());
            return true;
        }
        catch (Exception ex)
        {
            problem = $"could not prepare {path} ({ex.Message})";
            return false;
        }
    }

    private static DirectorySecurity Security(bool usersRead = false)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        if (usersRead)
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }
}

/// <summary>What survives the service being replaced. Lives in AutoUpdater.Folder.</summary>
internal sealed class UpdateRecord
{
    /// <summary>The version setup was started for and has not been seen to finish, or null.</summary>
    [JsonPropertyName("inFlight")] public string? InFlight { get; set; }

    /// <summary>The version last attempted, and how many times in a row.</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("attempts")] public int Attempts { get; set; }

    [JsonPropertyName("from")] public string? From { get; set; }

    /// <summary>The session the app was open in when setup started - it is started there again.</summary>
    [JsonPropertyName("session")] public uint? Session { get; set; }
    [JsonPropertyName("startedAt")] public long StartedAt { get; set; }

    /// <summary>The updater window watched this attempt and starts the app itself; the service is only the fallback.</summary>
    [JsonPropertyName("attended")] public bool Attended { get; set; }

    /// <summary>Releases the service will not install; the app offers them in its own window.</summary>
    [JsonPropertyName("refused")] public List<string> Refused { get; set; } = [];

    public static UpdateRecord Read()
    {
        try
        {
            return File.Exists(AutoUpdater.RecordPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(AutoUpdater.RecordPath), UpdateRecordJsonContext.Default.UpdateRecord) ?? new()
                : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    public void Write()
    {
        try
        {
            if (!PrivateFolder.Ensure(AutoUpdater.Folder, out _)) return;
            AtomicFile.WriteAllText(AutoUpdater.RecordPath, JsonSerializer.Serialize(this, UpdateRecordJsonContext.Default.UpdateRecord));
        }
        catch (Exception)
        {
            // Lost: the worst case is one attempt counted twice, or not at all.
        }
    }
}

[JsonSerializable(typeof(UpdateRecord))]
internal partial class UpdateRecordJsonContext : JsonSerializerContext;
