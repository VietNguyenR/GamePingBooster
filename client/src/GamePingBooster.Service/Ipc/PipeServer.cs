using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.Service.Ipc;

/// <summary>
/// The bridge between the UI (normal user rights) and the engine (LocalSystem).
///
/// Model: every message is one line of JSON. The UI sends commands, the service replies with
/// state, and the service also pushes state whenever something changes (game start/stop, ping
/// updates, connection loss).
///
/// Security: the pipe ACL only grants the local Users group read/write. The service accepts no
/// file paths and no arbitrary commands from the UI - just four fixed verbs, with every
/// parameter checked against the profile. This is a privilege boundary; keep it narrow.
/// </summary>
internal sealed class PipeServer
{
    private readonly TunnelEngine _engine;
    private readonly Action<string> _log;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PipeServer(TunnelEngine engine, Action<string> log)
    {
        _engine = engine;
        _log = log;
        _engine.StatusChanged += status => _ = PushAsync(status);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
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
                _log("UI disconnected.");
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
                await _engine.DisconnectAsync().ConfigureAwait(false);
                break;

            case "reload-profile":
                try
                {
                    await _engine.LoadProfileAsync(ct).ConfigureAwait(false);
                    _log("Profile reloaded.");
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

            default:
                _log($"Unsupported verb: {cmd.Verb}");
                break;
        }
    }

    /// <summary>Sends a status snapshot once a second while a UI is attached.</summary>
    private async Task PushPeriodicallyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await PushAsync(_engine.Snapshot()).ConfigureAwait(false);
        }
    }

    /// <summary>Pushes one status message to the UI. Swallows errors because the UI may be gone.</summary>
    public async Task PushAsync(StatusMessage status)
    {
        var writer = _writer;
        if (writer is null) return;

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
