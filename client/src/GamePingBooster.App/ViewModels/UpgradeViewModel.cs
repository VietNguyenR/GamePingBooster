using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// The upgrade screen: pick a plan, get a QR, and watch the order turn paid.
///
/// The same purchase as gamepingbooster.com/checkout - the same Payment row, made by the same
/// server function - only on a screen in the app. Two consequences worth stating:
///
///   - Nothing here decides a price or a result. Prices come from GET /app/plans, the amount and the
///     QR from the order the server made, and "paid" from the server reading SePay's webhook.
///   - Nothing here signs anybody in. The refresh token the app already holds is the credential,
///     which is why paying here needs no second sign-in - and why a webview, with its own cookie
///     jar and its own login page, was not used.
/// </summary>
public sealed class UpgradeViewModel : INotifyPropertyChanged
{
    private readonly string _licenceUrl;
    private readonly PurchaseWatcher _watcher;
    private readonly Func<string?> _refreshToken;
    private readonly Func<string, LicenceClient> _clientFor;

    /// <summary>For the QR image only. Separate from the licence client: a different host.</summary>
    private static readonly HttpClient QrHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public UpgradeViewModel(string licenceUrl, PurchaseWatcher watcher,
        Func<string?>? refreshToken = null, Func<string, LicenceClient>? clientFor = null)
    {
        _licenceUrl = licenceUrl;
        _watcher = watcher;
        _refreshToken = refreshToken ?? RefreshTokenStore.Load;
        _clientFor = clientFor ?? (url => new LicenceClient(url));
        _watcher.Updated += OnWatcherUpdate;
    }

    /// <summary>Unsubscribes from the watcher. The watching itself goes on - see PurchaseWatcher.</summary>
    public void Detach() => _watcher.Updated -= OnWatcherUpdate;

    private static string Lang => Loc.Current == AppLanguage.Vietnamese ? "vi" : "en";

    // ------------------------------------------------------------------------ state

    public enum Screen { Loading, Choose, Pay, Paid, Closed, Blocked, Failed }

    private Screen _screen = Screen.Loading;
    public Screen Current
    {
        get => _screen;
        private set
        {
            if (!Set(ref _screen, value)) return;
            Raise(nameof(IsLoading));
            Raise(nameof(IsChoose));
            Raise(nameof(IsPay));
            Raise(nameof(IsPaid));
            Raise(nameof(IsClosed));
            Raise(nameof(IsBlocked));
            Raise(nameof(IsFailed));
            Raise(nameof(HasNotice));
        }
    }

    public bool IsLoading => Current == Screen.Loading;
    public bool IsChoose => Current == Screen.Choose;
    public bool IsPay => Current == Screen.Pay;
    public bool IsPaid => Current == Screen.Paid;
    public bool IsClosed => Current == Screen.Closed;
    public bool IsBlocked => Current == Screen.Blocked;
    public bool IsFailed => Current == Screen.Failed;

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) Raise(nameof(NotBusy)); }
    }
    public bool NotBusy => !Busy;

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(Error);

    private string _message = "";
    /// <summary>The sentence of a blocked, closed or failed screen.</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    /// <summary>
    /// Why this window opened by itself, shown above the plans: a sign-in that found the account
    /// with no plan (see LoginViewModel.NeedsPlan). Null when somebody chose to open it.
    /// </summary>
    public string? Notice { get; init; }

    /// <summary>Only while choosing: once an order exists, "your account has expired" is old news.</summary>
    public bool HasNotice => !string.IsNullOrEmpty(Notice) && (IsLoading || IsChoose);

    /// <summary>
    /// The notice for a 402 from /auth/token. "expired" - and a server older than the codes - gets
    /// the app's own sentence, because the server's sends people to the website from inside a
    /// window that sells plans. Not verified, or an address that earns no trial, keep the server's:
    /// it names the thing to do, and buying is still a way out of both.
    /// </summary>
    public static string NoticeFor(LicenceException refusal) =>
        refusal.Code is null or "expired" ? Loc.T("upgrade.expired") : refusal.Message;

    // ------------------------------------------------------------------------ choosing

    private List<PlanGroup> _groups = [];
    public List<MonthTab> Tabs { get; private set; } = [];
    public List<PlanCard> Cards { get; private set; } = [];

    private string? _discountText;
    public string? DiscountText
    {
        get => _discountText;
        private set { if (Set(ref _discountText, value)) Raise(nameof(HasDiscount)); }
    }
    public bool HasDiscount => !string.IsNullOrEmpty(DiscountText);

    private int _months;
    private string? _selectedCode;

    public PlanCard? Selected => Cards.FirstOrDefault(c => c.Code == _selectedCode);
    public string TotalText => Selected is { } c ? Money(c.AmountMinor) : "-";
    public bool CanPay => Selected is not null && !Busy && Agreed;

    private bool _agreed;

    /// <summary>
    /// The terms and refund policy ticked, as on the web checkout. Not remembered: every new order
    /// is agreed to afresh. An order already open was agreed to when it was made, so reopening the
    /// window onto it does not ask again.
    /// </summary>
    public bool Agreed
    {
        get => _agreed;
        set { if (Set(ref _agreed, value)) Raise(nameof(CanPay)); }
    }

    /// <summary>The licence server's site, where the terms live: gamepingbooster.com in production.</summary>
    private string Site => Uri.TryCreate(_licenceUrl, UriKind.Absolute, out var u)
        ? u.GetLeftPart(UriPartial.Authority)
        : "https://gamepingbooster.com";

    public string TermsUrl => Site + "/terms-of-service";
    public string RefundUrl => Site + "/refund-policy";

    // ------------------------------------------------------------------------ paying

    private OrderInfo? _order;
    public string InvoiceNumber => _order?.InvoiceNumber ?? "";
    public string PlanName => _order?.Plan?.Name ?? "";
    public string AmountText => _order is null ? "" : Money(_order.AmountMinor);
    /// <summary>The amount as digits only, for pasting into a banking app's amount field.</summary>
    public string AmountDigits => _order?.AmountMinor.ToString(CultureInfo.InvariantCulture) ?? "";
    public string BankText => _order?.Transfer?.BankCode ?? "";
    public string AccountNumber => _order?.Transfer?.AccountNumber ?? "";
    public string Holder => _order?.Transfer?.Holder ?? "";
    public string Memo => _order?.Transfer?.Memo ?? "";
    public bool AmountMismatch => _order?.AmountMismatch == true;
    public string MismatchText => _order is { PaidAmountMinor: { } paid }
        ? Loc.F("upgrade.pay.mismatch", Money(paid), Money(_order.AmountMinor))
        : "";

    private Bitmap? _qr;
    public Bitmap? Qr
    {
        get => _qr;
        private set { if (Set(ref _qr, value)) { Raise(nameof(HasQr)); Raise(nameof(QrMissing)); } }
    }
    public bool HasQr => Qr is not null;

    private bool _qrFailed;
    public bool QrMissing => !HasQr && _qrFailed;

    private string _watchText = "";
    public string WatchText { get => _watchText; private set => Set(ref _watchText, value); }

    private string _paidText = "";
    public string PaidText { get => _paidText; private set => Set(ref _paidText, value); }

    /// <summary>Set when a paid order has been seen, so the account window can reload.</summary>
    public bool Purchased { get; private set; }

    // ------------------------------------------------------------------------ actions

    /// <summary>
    /// Opens the screen. An order the watcher is still waiting on comes back as it was - closing
    /// the window and reopening it must not lose the QR somebody is halfway through paying.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_watcher.Current is { } open && _watcher.LicenceUrl == _licenceUrl && (_watcher.Watching || open.IsPaid))
        {
            await ShowOrderAsync(open, ct).ConfigureAwait(true);
            return;
        }
        await LoadPlansAsync(ct).ConfigureAwait(true);
    }

    public async Task LoadPlansAsync(CancellationToken ct)
    {
        Current = Screen.Loading;
        Error = null;
        var token = _refreshToken();
        if (token is null)
        {
            Fail(Loc.T("upgrade.err.signedOut"));
            return;
        }

        try
        {
            using var client = _clientFor(_licenceUrl);
            var plans = await client.FetchPlansAsync(token, Lang, ct).ConfigureAwait(true);

            if (!plans.Purchasable)
            {
                Message = plans.BlockedReason == "creator" ? Loc.T("upgrade.blocked.creator") : Loc.T("upgrade.blocked.suspended");
                Current = Screen.Blocked;
                return;
            }

            _groups = plans.Groups.Where(g => g.Plans.Count > 0).ToList();
            if (_groups.Count == 0)
            {
                Fail(Loc.T("upgrade.err.noPlans"));
                return;
            }

            DiscountText = plans.Discount is { } d
                ? Loc.F("upgrade.discount", d.Percent > 0 ? $"{d.Percent}%" : Money(d.AmountMinor), d.Code)
                : null;

            // Start where somebody is: the length and tier of the plan they have, else the cheapest.
            var current = _groups.SelectMany(g => g.Plans).FirstOrDefault(p => p.Code == plans.CurrentPlanCode);
            var start = current ?? _groups[0].Plans[0];
            _months = start.Months;
            _selectedCode = start.Code;
            Rebuild();
            Current = Screen.Choose;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(Describe(ex));
        }
    }

    public void PickMonths(int months)
    {
        var group = _groups.FirstOrDefault(g => g.Months == months);
        if (group is null) return;
        // Keep the tier across lengths, as the website does: "Đôi bạn, 1 tháng" -> "Đôi bạn, 3 tháng".
        var tier = Selected?.Tier;
        _months = months;
        _selectedCode = (group.Plans.FirstOrDefault(p => p.Tier == tier) ?? group.Plans[0]).Code;
        Rebuild();
    }

    public void PickPlan(string code)
    {
        if (Cards.All(c => c.Code != code)) return;
        _selectedCode = code;
        Rebuild();
    }

    /// <summary>Asks the server for the order and shows its QR.</summary>
    public async Task PayAsync(CancellationToken ct)
    {
        if (Selected is not { } plan || Busy || !Agreed) return;
        var token = _refreshToken();
        if (token is null)
        {
            Fail(Loc.T("upgrade.err.signedOut"));
            return;
        }

        Busy = true;
        Error = null;
        Raise(nameof(CanPay));
        try
        {
            using var client = _clientFor(_licenceUrl);
            var result = await client.CreateOrderAsync(token, plan.Code, Lang, ct).ConfigureAwait(true);
            if (result.Order is not { } order) throw new LicenceException(Loc.Vi("licenceErr.emptyAnswer"));
            _watcher.Watch(_licenceUrl, order);
            await ShowOrderAsync(order, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (LicenceException ex) when (ex.Code is "creator" or "suspended")
        {
            Message = ex.Message;
            Current = Screen.Blocked;
        }
        catch (Exception ex)
        {
            // Stays on the plan list with the reason, so pressing again is the retry.
            Error = Describe(ex);
        }
        finally
        {
            Busy = false;
            Raise(nameof(CanPay));
        }
    }

    /// <summary>Cancels the open order and goes back to the plans.</summary>
    public async Task CancelOrderAsync(CancellationToken ct)
    {
        if (_order is null || Busy) return;
        var token = _refreshToken();
        if (token is null)
        {
            Fail(Loc.T("upgrade.err.signedOut"));
            return;
        }

        Busy = true;
        Error = null;
        try
        {
            using var client = _clientFor(_licenceUrl);
            var result = await client.CancelOrderAsync(token, _order.InvoiceNumber, Lang, ct).ConfigureAwait(true);
            if (result.Order is { } order)
            {
                _watcher.Replace(order);
                // A transfer that landed while the button was being pressed wins: show that it is paid.
                if (order.IsPaid)
                {
                    await ShowOrderAsync(order, ct).ConfigureAwait(true);
                    return;
                }
            }
            _watcher.Clear();
            _order = null;
            Qr = null;
            await LoadPlansAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>From a closed order or a failure: back to the plan list.</summary>
    public Task StartOverAsync(CancellationToken ct)
    {
        _watcher.Clear();
        _order = null;
        Qr = null;
        return LoadPlansAsync(ct);
    }

    // ------------------------------------------------------------------------ internals

    private async Task ShowOrderAsync(OrderInfo order, CancellationToken ct)
    {
        ApplyOrder(order);
        if (Current != Screen.Pay || order.Transfer is null) return;

        _qrFailed = false;
        Raise(nameof(QrMissing));
        Qr = await LoadQrAsync(order.Transfer.QrUrl, ct).ConfigureAwait(true);
        if (Qr is null)
        {
            _qrFailed = true;
            Raise(nameof(QrMissing));
        }
    }

    private void ApplyOrder(OrderInfo order)
    {
        _order = order;
        Raise(nameof(InvoiceNumber));
        Raise(nameof(PlanName));
        Raise(nameof(AmountText));
        Raise(nameof(BankText));
        Raise(nameof(AccountNumber));
        Raise(nameof(Holder));
        Raise(nameof(Memo));
        Raise(nameof(AmountMismatch));
        Raise(nameof(MismatchText));

        if (order.IsPaid)
        {
            Purchased = true;
            Qr = null;
            PaidText = order.ActiveUntil is { } until
                ? Loc.F("upgrade.paid.until", order.Plan?.Name ?? "",
                    DateTimeOffset.FromUnixTimeSeconds(until).LocalDateTime.ToString(Loc.T("format.dateTime"), CultureInfo.InvariantCulture))
                : Loc.F("upgrade.paid.plain", order.Plan?.Name ?? "");
            Current = Screen.Paid;
        }
        else if (order.IsClosed)
        {
            Qr = null;
            Message = order.Status == "FAILED" ? Loc.T("upgrade.closed.failed") : Loc.T("upgrade.closed.cancelled");
            Current = Screen.Closed;
        }
        else
        {
            WatchText = Loc.T("upgrade.pay.waiting");
            Current = Screen.Pay;
        }
    }

    private void OnWatcherUpdate(PurchaseUpdate update)
    {
        // Only about the order on this screen. A stale update for an order already replaced is dropped.
        if (update.Order is { } order && _order is not null && order.InvoiceNumber != _order.InvoiceNumber) return;

        if (update.Order is { } o && (o.IsPaid || o.IsClosed || o.Status != _order?.Status || o.AmountMismatch != _order?.AmountMismatch))
        {
            ApplyOrder(o);
        }

        if (Current != Screen.Pay) return;
        if (update.GaveUp)
        {
            WatchText = Loc.T("upgrade.pay.gaveUp");
        }
        else if (update.Error is { } error)
        {
            WatchText = update.Transient ? error : Loc.F("upgrade.pay.stopped", error);
        }
        else
        {
            WatchText = Loc.T("upgrade.pay.waiting");
        }
    }

    private static async Task<Bitmap?> LoadQrAsync(string url, CancellationToken ct)
    {
        // The URL comes from our own server, but it is still a URL handed to an HTTP client: only
        // https to a real host, and only an image back.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            using var response = await QrHttp.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return null;
            if (response.Content.Headers.ContentLength is > 2_000_000) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // The transfer details are on screen as text; a missing picture is not a failed purchase.
            return null;
        }
    }

    private void Rebuild()
    {
        var group = _groups.FirstOrDefault(g => g.Months == _months) ?? _groups[0];
        _months = group.Months;
        if (group.Plans.All(p => p.Code != _selectedCode)) _selectedCode = group.Plans[0].Code;

        Tabs = _groups.Select(g => new MonthTab(
            g.Months,
            Loc.F(g.Months == 1 ? "upgrade.months.one" : "upgrade.months.many", g.Months),
            g.BestSaving is { } s ? Loc.F("upgrade.saveUpTo", s) : null,
            g.Months == _months)).ToList();

        Cards = group.Plans.Select(p => new PlanCard(
            p.Code,
            p.Tier,
            p.Name,
            Money(p.AmountMinor),
            p.AmountMinor < p.PriceMinor ? Money(p.PriceMinor) : null,
            p.Months > 1 ? Loc.F("upgrade.perMonth", Money(p.MonthlyMinor)) : null,
            p.SavingPercent is { } pct ? Loc.F("upgrade.save", pct) : null,
            Loc.F(p.DeviceLimit == 1 ? "upgrade.devices.one" : "upgrade.devices.many", p.DeviceLimit),
            p.AmountMinor,
            p.Code == _selectedCode)).ToList();

        Raise(nameof(Tabs));
        Raise(nameof(Cards));
        Raise(nameof(Selected));
        Raise(nameof(TotalText));
        Raise(nameof(CanPay));
    }

    private void Fail(string message)
    {
        Message = message;
        Current = Screen.Failed;
    }

    private string Describe(Exception ex) => ex switch
    {
        LicenceException { Code: "rate_limited" } => Loc.T("upgrade.err.rateLimited"),
        LicenceException le => le.Message,
        TimeoutException te => te.Message,
        _ => Loc.F("account.unreachable", _licenceUrl, ex.Message),
    };

    /// <summary>"49.000đ" in Vietnamese, "49,000 VND" in English. Invariant culture only - see Loc.F.</summary>
    public static string Money(long minor)
    {
        var grouped = minor.ToString("N0", CultureInfo.InvariantCulture);
        return Loc.Current == AppLanguage.Vietnamese ? grouped.Replace(',', '.') + "đ" : grouped + " VND";
    }

    // --------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One "1 tháng / 3 tháng / 6 tháng" tab.</summary>
public sealed record MonthTab(int Months, string Label, string? SavingText, bool IsSelected)
{
    public bool HasSaving => SavingText is not null;
}

/// <summary>One plan on the upgrade screen.</summary>
public sealed record PlanCard(
    string Code,
    string Tier,
    string Name,
    string PriceText,
    string? StrikeText,
    string? MonthlyText,
    string? SavingText,
    string DevicesText,
    long AmountMinor,
    bool IsSelected)
{
    public bool HasStrike => StrikeText is not null;
    public bool HasMonthly => MonthlyText is not null;
    public bool HasSaving => SavingText is not null;
}
