using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Native;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item 5: the power plan, the network card's power saving and the battery (pc-checklist-handoff.md). Windows holding
/// the CPU back for power, or a card napping between packets, shows up as stutter and spikes no route can fix.
///
/// All three are read here, locally (PowerPlan, NicPowerSaving - the same code the service uses before it changes
/// anything), every <see cref="Every"/>: a few powrprof calls and a registry walk, nothing per process. Unlike the other
/// items they need no Connect to be known, so the line is filled from the start.
///
/// What the app can do, through the service (the changes need LocalSystem and must outlive the app):
/// - "Chuyển hiệu năng cao": a faster plan for the game session, put back by the service when the game closes (or
///   boosting stops before a game ran). Offered only in a session - outside one there is nothing to end it.
/// - "Tắt tiết kiệm điện": the card's power-saving settings off, lasting; the card restarts, so the press asks once
///   more and is offered only with boosting stopped and no game running (a card restart under a live tunnel broke the
///   uplink for 95 s, measured 2026-10-17). The line is known before Connect, which is when a player sees it.
///   "Bật lại" / "Khôi phục" puts the old values back, under the same rule.
/// - The battery is advice only.
/// </summary>
public sealed class PowerCheck : CheckItem
{
    private static readonly Geometry BoltIcon = Geometry.Parse("M9,1 L3,9 H8 L7,15 L13,7 H8 Z");

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CardEvery = TimeSpan.FromSeconds(30);

    /// <summary>No answer from the service in this long (an older one without the verb): the press is given up.</summary>
    private static readonly TimeSpan BoostTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NicTimeout = TimeSpan.FromSeconds(60);

    public override string Id => "power";
    public override Geometry Icon => BoltIcon;
    public override string Title => T("check.power.title");

    private readonly PipeClient _pipe;

    // Read on the thread pool, used on the UI thread.
    private volatile Reading? _reading;

    // From the service. UI thread.
    private bool _inSession;
    private bool _tunnelUp;
    private bool _gameRunning;
    private bool _boosted;
    private bool _nicChanged;
    private bool _serviceKnowsPower;

    // Presses. UI thread.
    private bool _boosting;
    private bool _boostFailed;
    private PowerPlan.Personality? _boostedFrom;
    private string? _nicBusy;      // "off" or "restore" while the service works
    private string? _nicOutcome;   // the last press's result: null ok, "reboot", or a failure
    /// <summary>"off" or "restore" while that card button reads "Chắc chắn?"; back after 5 s. Both restart the card.</summary>
    private string? _nicConfirm;
    private DispatcherTimer? _confirmTimer;
    private DispatcherTimer? _pressTimer;

    private sealed record Reading(PowerPlan.State Plan, bool CanBoost, NicPowerSaving.Card? Card);

    public PowerCheck(PipeClient pipe)
    {
        _pipe = pipe;
        _ = Task.Run(MeasureAsync);
    }

    // ------------------------------------------------------------ service

    public override void OnStatus(StatusMessage status)
    {
        if (status.AckVerb == "power-boost")
        {
            _boosting = false;
            _boostFailed = status.CommandError is not null && status.PowerBoosted == false;
            _pressTimer?.Stop();
            Remeasure();
        }
        if (status.AckVerb == "nic-power")
        {
            _nicOutcome = status.CommandError;
            _nicBusy = null;
            _pressTimer?.Stop();
            Remeasure(card: true);
        }

        _serviceKnowsPower = true;
        _inSession = status.GameRunning || status.State is TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting;
        _gameRunning = status.GameRunning;
        _tunnelUp = status.State is not (TunnelState.Disconnected or TunnelState.Faulted);
        var changed = status.PowerBoosted != _boosted || status.NicPowerChanged != _nicChanged;
        if (status.PowerBoosted != _boosted && !status.PowerBoosted) _boostedFrom = null;
        _boosted = status.PowerBoosted;
        _nicChanged = status.NicPowerChanged;
        if (changed) Remeasure(card: true);
        RaiseActions();
        Evaluate();
    }

    public override void OnServiceGone()
    {
        _serviceKnowsPower = false;
        _inSession = _tunnelUp = _gameRunning = _boosted = _nicChanged = false;
        _boosting = false;
        _nicBusy = null;
        RaiseActions();
        Evaluate();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Evaluate();
    }

    // ------------------------------------------------------------ measuring (thread pool)

    /// <summary>One read at a time: the tick and a read after a press must not finish out of order.</summary>
    private readonly SemaphoreSlim _reads = new(1, 1);
    private DateTime _cardAt = DateTime.MinValue;

    private async Task MeasureAsync()
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            await ReadAsync(card: false);
        }
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>
    /// Reads the plan (and the card when asked, or every <see cref="CardEvery"/>) and shows it. The card kept is always
    /// the last one read, whoever read it - a tick must never put back a card read before a press.
    /// </summary>
    private async Task ReadAsync(bool card)
    {
        await _reads.WaitAsync();
        try
        {
            var plan = PowerPlan.Read();
            var c = _reading?.Card;
            if (card || DateTime.UtcNow - _cardAt >= CardEvery)
            {
                c = NicPowerSaving.LineCard();
                _cardAt = DateTime.UtcNow;
            }
            _reading = new Reading(plan, !plan.IsFast && PowerPlan.BoostTarget(plan) is not null, c);
            Dispatcher.UIThread.Post(Evaluate);
        }
        catch (Exception)
        {
            // A read that fails leaves the last one shown; the next tick tries again.
        }
        finally
        {
            _reads.Release();
        }
    }

    /// <summary>Reads again now instead of at the next tick - after a press, so the line shows the result at once.</summary>
    private void Remeasure(bool card = false) => _ = Task.Run(() => ReadAsync(card));

    // ------------------------------------------------------------ fix / restore

    public override string? FixLabel => PlanProblem(_reading) ? (_boosting ? T("check.power.boosting") : T("check.power.boost")) : null;

    public override bool CanFix => PlanProblem(_reading) && _reading!.CanBoost && _inSession && !_boosting && _serviceKnowsPower;

    public override async Task FixAsync()
    {
        if (!CanFix) return;
        _boosting = true;
        _boostFailed = false;
        _boostedFrom = _reading?.Plan.Type;
        StartPressTimer(BoostTimeout, () => { _boosting = false; _boostFailed = true; });
        RaiseActions();
        Evaluate();
        try
        {
            await _pipe.SendAsync(new CommandMessage { Verb = "power-boost", Enabled = true });
        }
        catch (Exception)
        {
            _pressTimer?.Stop();
            _boosting = false;
            _boostFailed = true;
            RaiseActions();
            Evaluate();
        }
    }

    /// <summary>The card may be restarted now: nothing rides on it and no other press is in flight.</summary>
    private bool CardFree => !_gameRunning && !_tunnelUp && _nicBusy is null && _serviceKnowsPower;

    public override bool CanRestore => _boosted || (_nicChanged && CardFree);

    public override async Task RestoreAsync()
    {
        if (_boosted) await _pipe.SendAsync(new CommandMessage { Verb = "power-boost", Enabled = false });
        if (_nicChanged && CardFree) await SendNicAsync(off: false);
    }

    private void AskNic(string what)
    {
        _nicConfirm = what;
        _confirmTimer?.Stop();
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmTimer?.Stop();
            _nicConfirm = null;
            Evaluate();
        };
        _confirmTimer.Start();
        Evaluate();
    }

    private void ConfirmNic(bool off)
    {
        _confirmTimer?.Stop();
        _nicConfirm = null;
        if (!CardFree) return;
        _ = SendNicAsync(off);
    }

    private async Task SendNicAsync(bool off)
    {
        _nicBusy = off ? "off" : "restore";
        _nicOutcome = null;
        StartPressTimer(NicTimeout, () => { _nicBusy = null; _nicOutcome = "timeout"; });
        RaiseActions();
        Evaluate();
        try
        {
            await _pipe.SendAsync(new CommandMessage { Verb = "nic-power", Enabled = off });
        }
        catch (Exception)
        {
            _pressTimer?.Stop();
            _nicBusy = null;
            _nicOutcome = "unreachable";
            RaiseActions();
            Evaluate();
        }
    }

    private void StartPressTimer(TimeSpan after, Action giveUp)
    {
        _pressTimer?.Stop();
        _pressTimer = new DispatcherTimer { Interval = after };
        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer?.Stop();
            giveUp();
            RaiseActions();
            Evaluate();
        };
        _pressTimer.Start();
    }

    // ------------------------------------------------------------ what the line says (UI thread)

    /// <summary>The plan holds the CPU back and the app did not already boost it. A plan of unknown type is not judged.</summary>
    private bool PlanProblem(Reading? r) =>
        r is { Plan.Readable: true } && !r.Plan.IsFast && r.Plan.Type != PowerPlan.Personality.Unknown && !_boosted;

    private void Evaluate()
    {
        var r = _reading;
        if (r is null)
        {
            Severity = CheckSeverity.Unknown;
            Summary = "…"; // the first read lands within milliseconds of the app starting
            Lines = [];
            return;
        }

        var problems = new List<string>();
        var lines = new List<DetailLine>();

        // The plan.
        if (PlanProblem(r))
        {
            problems.Add(Loc.F("check.power.problemPlan", PlanName(r.Plan)));
            lines.Add(new DetailLine(T("check.power.planWhy")));
            if (!r.CanBoost) lines.Add(new DetailLine(T("check.power.boostNone")));
            else if (!_inSession) lines.Add(new DetailLine(T("check.power.boostNeedsSession")));
        }
        if (_boosted)
        {
            lines.Add(new DetailLine(_boostedFrom is { } from && from != PowerPlan.Personality.Unknown
                ? Loc.F("check.power.boosted", NameOf(from), PlanName(r.Plan))
                : Loc.F("check.power.boostedPlain", PlanName(r.Plan))));
        }
        if (_boostFailed && !_boosted) lines.Add(new DetailLine(T("check.power.boostFailed")));

        // The network card.
        var card = r.Card;
        if (card is { On.Count: > 0 })
        {
            problems.Add(T("check.power.problemNic"));
            lines.Add(new DetailLine(Loc.F("check.power.nicOn", card.Description)));
            lines.AddRange(card.On.Select(s => new DetailLine("• " + s.Name, IsEntry: true)));
            lines.Add(new DetailLine(T("check.power.nicWhy")));
            if (_nicBusy is not null)
                lines.Add(new DetailLine(T(_nicBusy == "off" ? "check.power.nicBusy" : "check.power.nicUndoBusy")));
            else if (_gameRunning)
                lines.Add(new DetailLine(T("check.power.nicGame")));
            else if (_tunnelUp)
                lines.Add(new DetailLine(T("check.power.nicTunnel")));
            else if (_serviceKnowsPower)
                lines.Add(new DetailLine("", Buttons: [_nicConfirm == "off"
                    ? new LineButton(T("check.power.nicConfirm"), () => ConfirmNic(off: true))
                    : new LineButton(T("check.power.nicOff"), () => AskNic("off"))]));
        }
        else if (_nicChanged)
        {
            var undo = CardFree
                ? new[] { _nicConfirm == "restore"
                    ? new LineButton(T("check.power.nicConfirm"), () => ConfirmNic(off: false))
                    : new LineButton(T("check.power.nicUndo"), () => AskNic("restore")) }
                : null;
            lines.Add(new DetailLine(_nicBusy == "restore" ? T("check.power.nicUndoBusy") : T("check.power.nicDone")));
            // On a line of its own, like the turn-off button: "Chắc chắn? Mạng ngắt vài giây" is too wide to share one.
            if (undo is not null) lines.Add(new DetailLine("", Buttons: undo));
        }
        if (_nicOutcome == "reboot") lines.Add(new DetailLine(T("check.power.nicReboot")));
        else if (_nicOutcome is not null && _nicOutcome is not ("game-running" or "tunnel-up")) lines.Add(new DetailLine(T("check.power.nicFailed")));

        // The battery: advice only.
        if (r.Plan.OnBattery)
        {
            problems.Add(T("check.power.problemBattery"));
            lines.Add(new DetailLine(T("check.power.battery")));
        }
        if (r.Plan.BatterySaver)
        {
            problems.Add(T("check.power.problemSaver"));
            lines.Add(new DetailLine(T("check.power.saver")));
        }

        if (problems.Count > 0)
        {
            Severity = CheckSeverity.Warning;
            Summary = string.Join(" · ", problems);
        }
        else
        {
            Severity = CheckSeverity.Good;
            Summary = Loc.F(_boosted ? "check.power.goodBoosted" : "check.power.good", PlanName(r.Plan));
        }
        Lines = lines;
        RaiseActions();
    }

    private static string PlanName(PowerPlan.State plan) =>
        plan.Type == PowerPlan.Personality.Balanced && plan.Overlay == PowerPlan.OverlayBestPerformance
            ? T("check.power.planBalancedBest")
            : NameOf(plan.Type);

    private static string NameOf(PowerPlan.Personality type) => type switch
    {
        PowerPlan.Personality.HighPerformance => T("check.power.planHigh"),
        PowerPlan.Personality.Balanced => T("check.power.planBalanced"),
        PowerPlan.Personality.PowerSaver => T("check.power.planSaver"),
        _ => T("check.power.planOther"),
    };
}
