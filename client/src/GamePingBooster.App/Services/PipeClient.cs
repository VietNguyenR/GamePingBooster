using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Services;

/// <summary>
/// The UI end of the IPC channel. It reconnects on its own if the service has not started yet
/// or has just restarted - the user should not see an error simply for opening the app early.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _readLoop;

    /// <summary>Raised whenever the service pushes a new status.</summary>
    public event Action<StatusMessage>? StatusReceived;

    /// <summary>Raised when the connection to the service is lost (stopped, or not installed).</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Runs in the background: connect, read messages, retry on failure.</summary>
    public void Start()
    {
        _readLoop ??= Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", IpcConstants.PipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(3000, ct).ConfigureAwait(false);

                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    try
                    {
                        var status = JsonSerializer.Deserialize(line, IpcJsonContext.Default.StatusMessage);
                        if (status is not null) StatusReceived?.Invoke(status);
                    }
                    catch (JsonException)
                    {
                        // Corrupt message - skip it and keep the connection.
                    }
                }

                Disconnected?.Invoke(Loc.T("pipe.closed"));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                Disconnected?.Invoke(Loc.T("pipe.unreachable"));
            }
            catch (Exception ex)
            {
                Disconnected?.Invoke(Loc.F("pipe.lost", ex.Message));
            }
            finally
            {
                _writer = null;
                _pipe?.Dispose();
                _pipe = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One writer at a time. Several parts of the app send on their own schedules - the profile sync,
    /// the token renewer, the connection-quality upload, the window - and two WriteLineAsync calls
    /// overlapping on one StreamWriter can interleave into a line the service cannot parse.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task SendAsync(CommandMessage command)
    {
        var writer = _writer;
        if (writer is null) throw new InvalidOperationException(Loc.T("pipe.notConnected"));

        var json = JsonSerializer.Serialize(command, IpcJsonContext.Default.CommandMessage);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task ConnectTunnelAsync(string? relayId = null, string? gameId = null)
        => SendAsync(new CommandMessage { Verb = "connect", RelayId = relayId, GameId = gameId });

    public Task DisconnectTunnelAsync() => SendAsync(new CommandMessage { Verb = "disconnect" });

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _pipe?.Dispose();
        _cts.Dispose();
    }
}
