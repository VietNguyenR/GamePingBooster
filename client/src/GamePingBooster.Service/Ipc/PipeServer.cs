using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Protocol;
using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Tunnel;
using GamePingBooster.Service.Update;

namespace GamePingBooster.Service.Ipc;

/// <summary>
/// The bridge between the UI (normal user rights) and the engine (LocalSystem).
///
/// Model: every message is one line of JSON. The UI sends commands, the service replies with
/// state, and the service also pushes state whenever something changes (game start/stop, ping
/// updates, connection loss).
///
/// Security: the pipe ACL only grants the local Users group read/write. The service accepts no
/// file paths and no arbitrary commands from the UI - seven fixed verbs, with every
/// parameter checked here rather than in the UI. This is a privilege boundary; keep it narrow.
///
/// Two of the seven carry a SECRET, and both are write-only: set-relay takes the pre-shared key,
/// set-token takes the licence token. Nothing ever sends either back up. Anything readable over
/// this pipe is readable by every process running as the user, which is the whole reason the
/// status message reports that a token EXISTS and when it expires, and never what it is.
/// </summary>
internal sealed class PipeServer
{
    private readonly TunnelEngine _engine;
    private readonly UnblockDns _unblock;
    private readonly LineBlockWatch _lineBlocks;
    private readonly AutoUpdater _updater;
    private readonly Network.NetworkApps _netApps;
    private readonly Network.PowerTune _power;
    private readonly Action<string> _log;
    private StreamWriter? _writer;
    private uint? _uiSession;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PipeServer(TunnelEngine engine, UnblockDns unblock, AutoUpdater updater, Network.NetworkApps netApps,
        Network.PowerTune power, LineBlockWatch lineBlocks, Action<string> log)
    {
        _engine = engine;
        _unblock = unblock;
        _lineBlocks = lineBlocks;
        _updater = updater;
        _netApps = netApps;
        _power = power;
        _log = log;
        _engine.StatusChanged += status => _ = PushAsync(status);
        _netApps.Changed += () => _ = PushAsync(_engine.Snapshot());
        _power.Changed += () => _ = PushAsync(_engine.Snapshot());
        _lineBlocks.Changed += () => _ = PushAsync(_engine.Snapshot());
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                // The session the app runs in: an update closes it, and the new service starts it again there.
                _uiSession = UserSession.OfPipeClient(pipe.SafePipeHandle);
                _log("UI connected.");

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                _writer = writer;

                // Push the current state as soon as the UI connects, so it never has to ask.
                await PushAsync(_engine.Snapshot()).ConfigureAwait(false);

                // Ping, loss and the packet counters change continuously, but StatusChanged only
                // fires on state transitions. Without this heartbeat the UI would show the
                // numbers captured at connect time and never move again.
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pump = PushPeriodicallyAsync(heartbeat.Token);

                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    {
                        await HandleLineAsync(line, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    heartbeat.Cancel();
                    try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }

                _writer = null;
                _uiSession = null;
                _log("UI disconnected.");

                // The app has closed (or crashed). The adapter kept between connects has no reason to stay.
                try
                {
                    _engine.ReleaseIdleAdapter();
                }
                catch (Exception ex)
                {
                    _log($"Could not remove the idle virtual adapter: {ex.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                _writer = null; // UI closed abruptly - wait for a new connection.
            }
            catch (Exception ex)
            {
                _log($"Pipe error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        CommandMessage? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize(line, IpcJsonContext.Default.CommandMessage);
        }
        catch (JsonException)
        {
            _log("Received a message that is not valid JSON - ignoring.");
            return;
        }
        if (cmd is null) return;

        if (cmd.Version != IpcConstants.ProtocolVersion)
        {
            await PushAsync(new StatusMessage
            {
                State = TunnelState.Faulted,
                Detail = "UI and service versions do not match",
                Error = $"The UI speaks protocol v{cmd.Version}, the service speaks v{IpcConstants.ProtocolVersion}. Reinstall the app.",
            }).ConfigureAwait(false);
            return;
        }

        switch (cmd.Verb)
        {
            case "connect":
                // Setup has been started and closes everything within seconds; a tunnel brought up now would
                // be torn down mid-connect. The status already says "installing", which the app shows.
                if (_updater.Installing)
                {
                    _log("connect refused: an update is being installed.");
                    await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
                    break;
                }
                try
                {
                    await _engine.ConnectAsync(cmd.RelayId, cmd.GameId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"connect failed: {ex.Message}");
                    // The engine already moved to Faulted and pushed the state; nothing more to do.
                }
                break;

            case "disconnect":
            // The unblock fix is deliberately NOT touched here. It is not part of the tunnel and
                // turning it off with the tunnel put the block back the moment somebody stopped
                // boosting - see Program, which starts it with the service instead.
                await _engine.DisconnectAsync().ConfigureAwait(false);
                break;

            case "reload-profile":
                try
                {
                    await _engine.LoadProfileAsync(ct).ConfigureAwait(false);
                    _log("Profile reloaded.");

                    // The profile carries the unblock list, so a new one may change what this
                    // machine claims. A no-op when it does not.
                    await _unblock.RefreshAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"Reloading the profile failed: {ex.Message}");
                }
                await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
                break;

            case "status":
                await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
                break;

            // The fifth verb, and the only one that carries data. It is WRITE-ONLY: the key goes
            // down, and nothing ever sends one back up. The pipe is open to BuiltinUsers, so a
            // readable key here would be readable by any process running as the user.
            //
            // The engine validates. This is a privilege boundary and the UI is on the wrong side
            // of it - anything at all can write to this pipe, so trusting the UI to have checked
            // would mean not checking.
            case "set-relay":
            {
                var error = await _engine
                    .SetRelayAsync(cmd.RelayEndpoints, cmd.Psk, cmd.LicenceUrl, ct)
                    .ConfigureAwait(false);
                if (error is not null) _log($"set-relay rejected: {error}");

                // Answered on the reply channel, not by overwriting the tunnel's Error. Saving
                // settings validates a format and writes a file; it does not touch the relay, so
                // the tunnel's last failure is neither this command's fault nor its verdict.
                // Reported the same way whether it worked or not, so the sender can tell the
                // reply from the heartbeat that lands a moment later.
                var reply = _engine.Snapshot();
                reply.AckVerb = "set-relay";
                reply.CommandError = error;
                await PushAsync(reply).ConfigureAwait(false);
                break;
            }

            // Same rule as set-relay: validate here, not in the UI. Anything at all can write to
            // this pipe, so trusting the sender to have checked would mean not checking.
            //
            // Note what is NOT validated, deliberately: whether the token is genuine. This side
            // cannot know - the signing key is on the licence server and the verifying key is on
            // the relay. All that happens here is a shape check, and the relay decides.
            case "set-token":
            {
                string? error = null;

                if (string.IsNullOrWhiteSpace(cmd.Token))
                {
                    // An explicit clear: signing out, or the UI discarding a token the licence
                    // server has revoked.
                    _engine.ClearToken();
                    _log("Licence token cleared.");
                }
                else if (cmd.Token.Length != GpbProtocol.TokenLen * 2)
                {
                    error = $"A licence token is {GpbProtocol.TokenLen * 2} hex characters, got {cmd.Token.Length}.";
                }
                else
                {
                    byte[]? raw = null;
                    try
                    {
                        raw = Convert.FromHexString(cmd.Token);
                    }
                    catch (FormatException)
                    {
                        error = "The licence token is not valid hex.";
                    }

                    if (raw is not null && !_engine.SetToken(raw))
                    {
                        error = "The licence token could not be stored. Check the service log.";
                    }
                }

                if (error is not null)
                {
                    // Never echo the token back, not even the part that was wrong. The message
                    // says what shape was expected and nothing about what arrived.
                    _log($"set-token rejected: {error}");
                    var bad = _engine.Snapshot();
                    bad.Error = error;
                    await PushAsync(bad).ConfigureAwait(false);
                }
                else
                {
                    await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
                }
                break;
            }

            // The seventh verb. Unlike the two above it, what it carries is NOT a secret: every
            // CIDR in a profile becomes a Windows route, so the whole list is readable with
            // Get-NetRoute while the tunnel is up. It is still validated here, because a file
            // that does not parse would leave the service unable to load any profile at all -
            // a denial of service any local process could trigger.
            // Everything one sync brought, stored together and reloaded once - see TunnelEngine.SetProfilesAsync. The
            // same untrusted-input rules as set-profile below, per envelope.
            case "set-profiles":
            {
                var error = await _engine.SetProfilesAsync(cmd.Profiles ?? [], ct).ConfigureAwait(false);
                if (error is not null) _log($"set-profiles: {error}");

                // Once per sync rather than once per game: the unblock list is the same in every game's profile.
                await _unblock.RefreshAsync(ct).ConfigureAwait(false);
                var status = _engine.Snapshot();
                status.Error = error;
                await PushAsync(status).ConfigureAwait(false);
                break;
            }

            case "set-profile":
            {
                if (string.IsNullOrWhiteSpace(cmd.Profile))
                {
                    var bad = _engine.Snapshot();
                    bad.Error = "No profile was sent.";
                    await PushAsync(bad).ConfigureAwait(false);
                    break;
                }

                var error = await _engine.SetProfileAsync(cmd.Profile, ct).ConfigureAwait(false);
                if (error is not null)
                {
                    _log($"set-profile rejected: {error}");
                    var bad = _engine.Snapshot();
                    bad.Error = error;
                    await PushAsync(bad).ConfigureAwait(false);
                }
                else
                {
                    // This is the push that matters most. A signed-in app fetches a profile per
                    // game seconds after the service started, which is where the server's unblock
                    // list actually arrives - the copy on disk at startup is the PREVIOUS one.
                    await _unblock.RefreshAsync(ct).ConfigureAwait(false);
                    await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
                }
                break;
            }

            // The connection-quality upload. The records are nobody's secret - they were written to be
            // sent away and carry no addresses - so reading them over a pipe open to BuiltinUsers gives
            // nothing away. The ids of an acknowledgement are held to their shape, because they become
            // file names.
            case "quality-outbox":
            {
                var reply = _engine.Snapshot();
                reply.AckVerb = "quality-outbox";
                if (!_engine.QualitySharing)
                {
                    reply.CommandError = "Connection-quality sharing is switched off.";
                }
                else if (_engine.QualityInMatch)
                {
                    reply.CommandError = "A match is being recorded - records are sent between matches.";
                }
                else
                {
                    var (items, pending) = QualityOutbox.Take(OutboxBatch, OutboxBatchChars);
                    reply.QualityOutbox = items;
                    reply.QualityPending = pending;
                }
                await PushAsync(reply).ConfigureAwait(false);
                break;
            }

            // Read-only, and nothing in it is secret: names, process names and region names, never a
            // range. Asked for by the "Supported games" window when it opens.
            case "games":
            {
                var reply = _engine.Snapshot();
                reply.AckVerb = "games";
                reply.Games = _engine.SupportedGames();
                await PushAsync(reply).ConfigureAwait(false);
                break;
            }

            // The relays the main window offers, and the saved choice among them. Read-only, and nothing in
            // it is secret: names and locations of relays whose addresses are in the routing table anyway.
            //
            // Answered once every relay has been pinged, and NOT awaited here: commands are handled one at a
            // time, and a second of pinging must never hold up a connect sent right behind it.
            case "relays":
                _ = Task.Run(async () =>
                {
                    var relays = await _engine.PingRelaysAsync().ConfigureAwait(false);
                    var reply = _engine.Snapshot();
                    reply.AckVerb = "relays";
                    reply.Relays = relays;
                    await PushAsync(reply).ConfigureAwait(false);
                }, ct);
                break;

            // Validated here like set-relay: anything at all can write to this pipe. It only chooses among
            // relays the profile already lists, and applies from the next connect.
            case "set-relay-choice":
            {
                var error = _engine.SetRelayChoice(cmd.RelayId);
                if (error is not null) _log($"set-relay-choice rejected: {error}");
                var reply = _engine.Snapshot();
                reply.AckVerb = "set-relay-choice";
                reply.CommandError = error;
                reply.Relays = _engine.RelayOptions();
                await PushAsync(reply).ConfigureAwait(false);
                break;
            }

            case "quality-ack":
            {
                var removed = QualityOutbox.Acknowledge(cmd.QualityIds ?? []);
                if (removed > 0) _log($"Connection quality: {removed} record(s) uploaded and removed from the queue.");
                var reply = _engine.Snapshot();
                reply.AckVerb = "quality-ack";
                await PushAsync(reply).ConfigureAwait(false);
                break;
            }

            case "set-quality-sharing":
            {
                var error = cmd.Enabled is { } enabled
                    ? _engine.SetQualitySharing(enabled)
                    : "Say whether sharing is on or off.";
                if (error is not null) _log($"set-quality-sharing rejected: {error}");
                var reply = _engine.Snapshot();
                reply.AckVerb = "set-quality-sharing";
                reply.CommandError = error;
                await PushAsync(reply).ConfigureAwait(false);
                break;
            }

            case "set-lobby-watch":
            {
                var error = cmd.Enabled is { } enabled
                    ? _engine.SetLobbyWatch(enabled)
                    : "Say whether the lobby log is on or off.";
                if (error is not null) _log($"set-lobby-watch rejected: {error}");
                var reply = _engine.Snapshot();
                reply.AckVerb = "set-lobby-watch";
                reply.CommandError = error;
                await PushAsync(reply).ConfigureAwait(false);
                break;
            }

            // PC tune-up panel, item 3. Read-only: process names and rates, never an address. Answered after the count
            // (a few seconds) and NOT awaited here, like relays: a connect sent right behind it must not wait.
            case "net-apps":
                _ = Task.Run(async () =>
                {
                    var (apps, error) = await _netApps.CountAsync(ct).ConfigureAwait(false);
                    var reply = _engine.Snapshot();
                    reply.AckVerb = "net-apps";
                    reply.NetApps = apps;
                    reply.CommandError = error;
                    await PushAsync(reply).ConfigureAwait(false);
                }, ct);
                break;

            // Pauses Windows Update for the length of the running game, or resumes it. No parameters beyond on/off:
            // which services, and for how long, are decided here. Not awaited: stopping a service takes seconds.
            case "pause-updates":
            {
                var on = cmd.Enabled ?? true;
                _ = Task.Run(async () =>
                {
                    string? error = null;
                    if (on) error = _netApps.PauseUpdates();
                    else _netApps.ResumeUpdates("the player pressed Restore");
                    if (error is not null) _log($"pause-updates refused: {error}");
                    var reply = _engine.Snapshot();
                    reply.AckVerb = "pause-updates";
                    reply.CommandError = error;
                    await PushAsync(reply).ConfigureAwait(false);
                }, ct);
                break;
            }

            // PC tune-up panel, item 5. On/off only: which plan, which card and which settings are decided here, never sent
            // by the app. Not awaited: a card restart takes seconds.
            case "power-boost":
            {
                var on = cmd.Enabled ?? true;
                _ = Task.Run(async () =>
                {
                    string? error = null;
                    if (on) error = _power.Boost();
                    else _power.Unboost("the player pressed Restore");
                    if (error is not null) _log($"power-boost refused: {error}");
                    var reply = _engine.Snapshot();
                    reply.AckVerb = "power-boost";
                    reply.CommandError = error;
                    await PushAsync(reply).ConfigureAwait(false);
                }, ct);
                break;
            }

            case "nic-power":
            {
                var off = cmd.Enabled ?? true;
                _ = Task.Run(async () =>
                {
                    var error = off ? await _power.TurnOffNicPowerSavingAsync().ConfigureAwait(false)
                        : await _power.RestoreNicAsync().ConfigureAwait(false);
                    if (error is not null) _log($"nic-power {(off ? "off" : "restore")}: {error}");
                    var reply = _engine.Snapshot();
                    reply.AckVerb = "nic-power";
                    reply.CommandError = error;
                    await PushAsync(reply).ConfigureAwait(false);
                }, ct);
                break;
            }

            // Only a request to look now. Which release, whether to install it and when are AutoUpdater's.
            case "check-update":
                _updater.CheckNow();
                await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
                break;

            // The updater window is open (the app starts it): check now and, with a release ready, install it with
            // the player watching. Carries nothing - the release, the file and whether it may run now are still
            // AutoUpdater's. Progress goes to UpdateProgress, which the window reads.
            case "update-now":
                _updater.UpdateNow();
                await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
                break;

            default:
                _log($"Unsupported verb: {cmd.Verb}");
                break;
        }
    }

    /// <summary>Records per quality-outbox reply, and roughly how much JSON: one pipe line carries them.</summary>
    private const int OutboxBatch = 20;
    private const int OutboxBatchChars = 400_000;

    /// <summary>Sends a status snapshot once a second while a UI is attached.</summary>
    private async Task PushPeriodicallyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
        }
    }

    /// <summary>The Windows session of the app attached now, or null when none is.</summary>
    public uint? UiSession => _writer is null ? null : _uiSession;

    /// <summary>Pushes one status message to the UI. Swallows errors because the UI may be gone.</summary>
    public async Task PushAsync(StatusMessage status)
    {
        var writer = _writer;
        if (writer is null) return;

        // Stamped here rather than in the engine's Snapshot: unblocking is not part of the tunnel
        // and the engine knows nothing about it. Every status leaves through this method, including
        // the ones the engine raises by itself, so this is the one place that cannot be forgotten
        // when a new verb is added.
        status.Unblock = _unblock.Enabled;
        status.UnblockDetail = _unblock.Detail;
        status.UnblockServices = [.. _unblock.Services];
        status.UnblockProblemCode = _unblock.ProblemCode;
        status.LineBlocks = [.. _lineBlocks.Blocked];
        status.UpdatesPaused = _netApps.UpdatesPaused;
        status.PowerBoosted = _power.Boosted;
        status.NicPowerChanged = _power.NicChanged;
        _updater.Stamp(status);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(status, IpcJsonContext.Default.StatusMessage);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _writer = null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Creates the pipe with an ACL: LocalSystem full control, the local Users group read/write.
    /// Not open to Everyone, which would include anonymous network accounts.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        security.AddAccessRule(new PipeAccessRule(users,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcConstants.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }
}
