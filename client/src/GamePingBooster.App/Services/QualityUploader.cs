using System.Net;
using System.Text.Json;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Services;

/// <summary>
/// Sends the service's connection-quality records to the licence server, between matches, on its own.
///
/// The split is the one ProfileSync has, the other way round. The service records every match and
/// every spike and keeps them in an outbox, but cannot send them: the licence server authenticates
/// with the account's refresh token, which belongs to the person and lives in their profile, out of
/// reach of LocalSystem. So this asks for a batch over the pipe, uploads it, and tells the service
/// which records the server took. A record leaves the outbox only then, so a closed app, a crash or a
/// server that is down loses nothing - it all goes up the next time this runs.
///
/// Never during a match. The service refuses the batch while one is being recorded, and this simply
/// tries again later: a match is the one time the player's line must carry nothing extra.
///
/// Quiet by design. Nothing here is the player's problem, so nothing is ever shown: a failure waits
/// and tries again, and a server that says "too many" or "not signed in" is left alone for a while.
/// </summary>
public sealed class QualityUploader : IAsyncDisposable
{
    private static readonly TimeSpan FirstAfter = TimeSpan.FromSeconds(45);
    /// <summary>
    /// Thirty seconds. Every pass is a pipe message on this machine and costs nothing when there is
    /// nothing to send - but uploads only happen BETWEEN matches, and PUBG leaves about forty seconds
    /// between one match's last packet and the next one's first. Two minutes missed most of those
    /// windows and let a whole evening pile up in the queue.
    /// </summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Batches per pass. A long evening's backlog goes up over a few passes, not in one burst.</summary>
    private const int MaxBatchesPerPass = 10;

    private readonly PipeClient _pipe;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private volatile StatusMessage? _last;
    private readonly object _gate = new();
    private string? _awaitingVerb;
    private TaskCompletionSource<StatusMessage>? _reply;
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public QualityUploader(PipeClient pipe)
    {
        _pipe = pipe;
    }

    /// <summary>Wired to PipeClient.StatusReceived. Keeps the latest status, and recognises our own replies.</summary>
    public void OnStatus(StatusMessage status)
    {
        _last = status;
        if (status.AckVerb is null) return;

        lock (_gate)
        {
            if (_reply is not null && status.AckVerb == _awaitingVerb) _reply.TrySetResult(status);
        }
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstAfter, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PassAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Network down, server down, pipe gone. The records are still in the outbox.
                    _quietUntil = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
                }

                await Task.Delay(Every, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _quietUntil) return;

        var status = _last;
        if (status is null || !_pipe.IsConnected) return;

        // Null is a service too old to have an outbox; false is the switch in Settings.
        if (status.QualitySharing is not true) return;
        if (string.IsNullOrWhiteSpace(status.LicenceUrl)) return;

        var refreshToken = RefreshTokenStore.Load();
        if (refreshToken is null) return;

        using var client = new LicenceClient(status.LicenceUrl);

        for (var batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            var reply = await AskAsync(new CommandMessage { Verb = "quality-outbox" }, "quality-outbox", ct)
                .ConfigureAwait(false);

            // A refusal here is the service saying "not now" - a match is on, or sharing is off.
            if (reply is null || reply.CommandError is not null) return;
            if (reply.QualityOutbox is not { Count: > 0 } items) return;

            // A record that is not JSON can never be sent. Removed rather than retried forever.
            var readable = new List<QualityOutboxItem>(items.Count);
            var broken = new List<string>();
            foreach (var item in items)
            {
                try
                {
                    using var _ = JsonDocument.Parse(item.Json);
                    readable.Add(item);
                }
                catch (JsonException)
                {
                    broken.Add(item.Id);
                }
            }
            if (broken.Count > 0) await AcknowledgeAsync(broken, ct).ConfigureAwait(false);
            if (readable.Count == 0) continue;

            QualityUploadResult result;
            try
            {
                result = await client.SendQualityAsync(refreshToken, status.DevicePublicKey, readable, ct)
                    .ConfigureAwait(false);
            }
            catch (LicenceException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _quietUntil = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
                return;
            }
            catch (LicenceException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                // Signed out, or a licence server older than the endpoint. Nothing to do until that changes.
                _quietUntil = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
                return;
            }

            var done = result.Accepted.Concat(result.Rejected).ToList();
            if (done.Count > 0) await AcknowledgeAsync(done, ct).ConfigureAwait(false);

            // Over the daily allowance: what fitted is acknowledged above, the rest waits.
            if (result.Limited)
            {
                _quietUntil = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
                return;
            }
            if (done.Count == 0) return;

            if (reply.QualityPending is { } pending && pending <= items.Count) return;
        }
    }

    private Task AcknowledgeAsync(List<string> ids, CancellationToken ct) =>
        AskAsync(new CommandMessage { Verb = "quality-ack", QualityIds = ids }, "quality-ack", ct);

    /// <summary>Sends a command and waits for the status that answers it, or null after a few seconds.</summary>
    private async Task<StatusMessage?> AskAsync(CommandMessage command, string verb, CancellationToken ct)
    {
        var reply = new TaskCompletionSource<StatusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _awaitingVerb = verb;
            _reply = reply;
        }

        try
        {
            await _pipe.SendAsync(command).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReplyTimeout);
            using (timeout.Token.Register(() => reply.TrySetCanceled()))
            {
                return await reply.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_reply, reply))
                {
                    _reply = null;
                    _awaitingVerb = null;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (Exception) { }
        }
        _cts.Dispose();
    }
}
