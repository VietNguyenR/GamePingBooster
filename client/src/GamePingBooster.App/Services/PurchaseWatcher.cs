using GamePingBooster.App.Services.Localization;

namespace GamePingBooster.App.Services;

/// <summary>
/// Watches the one order bought in the app until the server says it is paid, and then gets the
/// new licence straight away.
///
/// It lives as long as the app, not as long as the upgrade window, on purpose. People close the
/// window and open their banking app; the transfer lands two minutes later. If the watching died
/// with the window, the order would still be paid (the server applies it from SePay's webhook,
/// whoever is looking) but this machine would go on presenting the trial's token until the next
/// half-life renewal - "paid but still on trial" is the support message this exists to prevent.
///
/// It only READS. Whether an order is paid is decided on the server, by the webhook; there is no
/// call here that could say otherwise, and a cracked copy of this class still gets nothing the
/// licence server did not sign.
/// </summary>
public sealed class PurchaseWatcher : IDisposable
{
    /// <summary>How often the order is asked about while it is fresh. One indexed lookup on the server.</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>After this, asked less often - nobody is still staring at the QR.</summary>
    public static TimeSpan SlowAfter { get; set; } = TimeSpan.FromMinutes(5);
    public static TimeSpan SlowPollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// And after this, not at all. The order stays open on the server and is still applied if money
    /// arrives; the next app start, or reopening the upgrade screen, picks it up.
    /// </summary>
    public static TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(30);

    private readonly Func<string?> _refreshToken;
    private readonly Action _onPaid;
    private readonly Action<Action> _post;
    private readonly Func<string, LicenceClient> _clientFor;

    private CancellationTokenSource? _cts;
    private readonly object _gate = new();

    /// <param name="refreshToken">The signed-in credential; RefreshTokenStore.Load in the app.</param>
    /// <param name="onPaid">Run once when the order turns PAID - TokenRefresher.RenewNow in the app.</param>
    /// <param name="post">Marshals an update onto the UI thread.</param>
    /// <param name="clientFor">Makes the HTTP client for a licence URL; replaceable for a check.</param>
    public PurchaseWatcher(Func<string?> refreshToken, Action onPaid, Action<Action> post,
        Func<string, LicenceClient>? clientFor = null)
    {
        _refreshToken = refreshToken;
        _onPaid = onPaid;
        _post = post;
        _clientFor = clientFor ?? (url => new LicenceClient(url));
    }

    /// <summary>Every change worth showing, on the UI thread.</summary>
    public event Action<PurchaseUpdate>? Updated;

    /// <summary>The order being watched, or the last one that finished; null before the first.</summary>
    public OrderInfo? Current { get; private set; }

    /// <summary>The licence server the current order belongs to.</summary>
    public string? LicenceUrl { get; private set; }

    /// <summary>True while an order is being polled.</summary>
    public bool Watching { get; private set; }

    /// <summary>
    /// Starts watching <paramref name="order"/>, replacing whatever was watched before. Only one at a
    /// time: the app shows one QR, and a second loop for an abandoned order would be noise.
    /// </summary>
    public void Watch(string licenceUrl, OrderInfo order)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = cts = new CancellationTokenSource();
            Current = order;
            LicenceUrl = licenceUrl;
            Watching = !order.IsPaid && !order.IsClosed;
        }

        if (order.IsPaid)
        {
            // Paid before anybody asked - a webhook faster than the screen. Still renew.
            _onPaid();
            return;
        }
        if (order.IsClosed) return;

        _ = Task.Run(() => LoopAsync(licenceUrl, order.InvoiceNumber, cts.Token));
    }

    /// <summary>Stops watching. The order itself is untouched.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            Watching = false;
        }
    }

    /// <summary>Forgets the last order, so a reopened upgrade screen starts at the plan list.</summary>
    public void Clear()
    {
        Stop();
        lock (_gate)
        {
            Current = null;
            LicenceUrl = null;
        }
    }

    /// <summary>Lets an order that came back from the server (a cancel, say) replace the one held.</summary>
    public void Replace(OrderInfo order)
    {
        if (order.IsClosed || order.IsPaid) Stop();
        Current = order;
        if (order.IsPaid) _onPaid();
    }

    private static string Lang => Loc.Current == AppLanguage.Vietnamese ? "vi" : "en";

    private async Task LoopAsync(string licenceUrl, string invoice, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var transientFailures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var age = DateTimeOffset.UtcNow - started;
                if (age > GiveUpAfter)
                {
                    Publish(new PurchaseUpdate { Order = Current, GaveUp = true, Finished = true }, ct);
                    return;
                }

                await Task.Delay(age > SlowAfter ? SlowPollInterval : PollInterval, ct).ConfigureAwait(false);

                var token = _refreshToken();
                if (token is null)
                {
                    // Signed out while waiting. Nothing to ask with, and nothing to give the answer to.
                    Publish(new PurchaseUpdate { Order = Current, Error = Loc.T("upgrade.err.signedOut"), Finished = true }, ct);
                    return;
                }

                try
                {
                    using var client = _clientFor(licenceUrl);
                    var result = await client.FetchOrderAsync(token, invoice, Lang, ct).ConfigureAwait(false);
                    if (result.Order is not { } order) continue;
                    transientFailures = 0;

                    var changed = Current is null
                        || Current.Status != order.Status
                        || Current.AmountMismatch != order.AmountMismatch
                        || Current.ActiveUntil != order.ActiveUntil;
                    if (ct.IsCancellationRequested) return;
                    Current = order;

                    if (order.IsPaid)
                    {
                        Watching = false;
                        _onPaid();
                        Publish(new PurchaseUpdate { Order = order, Finished = true }, ct, force: true);
                        return;
                    }
                    if (order.IsClosed)
                    {
                        Watching = false;
                        Publish(new PurchaseUpdate { Order = order, Finished = true }, ct, force: true);
                        return;
                    }
                    if (changed) Publish(new PurchaseUpdate { Order = order }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (LicenceException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                                      or System.Net.HttpStatusCode.NotFound)
                {
                    // The sign-in ended, or the order is not this account's any more. Neither fixes
                    // itself by asking again.
                    Watching = false;
                    Publish(new PurchaseUpdate { Order = Current, Error = ex.Message, Finished = true }, ct);
                    return;
                }
                catch (Exception ex)
                {
                    // A timeout, a dropped connection, a 5xx. The order is safe on the server; say so
                    // once and keep asking - the next answer clears it.
                    transientFailures++;
                    if (transientFailures == 2)
                    {
                        Publish(new PurchaseUpdate { Order = Current, Error = Loc.F("upgrade.err.reconnecting", ex.Message), Transient = true }, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Replaced or stopped.
        }
        finally
        {
            lock (_gate)
            {
                if (_cts is not null && _cts.Token == ct) Watching = false;
            }
        }
    }

    private void Publish(PurchaseUpdate update, CancellationToken ct, bool force = false)
    {
        // A loop that has been replaced must not report over the new order - unless it is reporting
        // the one thing that matters whoever is watching: that money arrived.
        if (ct.IsCancellationRequested && !force) return;
        _post(() => Updated?.Invoke(update));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}

/// <summary>One thing the watcher has to say.</summary>
public sealed class PurchaseUpdate
{
    public OrderInfo? Order { get; init; }

    /// <summary>A sentence for the screen, when something went wrong.</summary>
    public string? Error { get; init; }

    /// <summary>The error is a network hiccup and the watching goes on.</summary>
    public bool Transient { get; init; }

    /// <summary>The watching has ended: paid, closed, refused, or given up.</summary>
    public bool Finished { get; init; }

    /// <summary>Stopped asking after <see cref="PurchaseWatcher.GiveUpAfter"/>; the order may still be paid.</summary>
    public bool GaveUp { get; init; }
}
