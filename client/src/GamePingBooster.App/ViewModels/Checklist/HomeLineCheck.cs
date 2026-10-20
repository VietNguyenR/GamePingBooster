using System.Globalization;
using Avalonia.Media;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item A "Mạng trong nhà": another device at home filling the line while the player plays - the one cause item 3 cannot
/// see, because item 3 only sees this PC. Read from the spike recorder (StatusMessage.HomeLine) in two ways:
/// - live, from Connect on - before the game is started, in the lobby, in a match (owner, 2026-10-21): the router's ping
///   floor rising with this PC quiet (HomeLineStatus.BusyNow / Busy*, Core.Quality.HomeLineWatch). Only the router's ping,
///   so it says "ping to the router rose", never "your ping rose".
/// - in a match, per spike: the game's own ping rose with the router busy (Household* / Congested*). Only this one
///   proves the game felt it, so only this one goes red while the game runs.
///
/// Two levels, agreed with the owner on 2026-10-20, and the worst outcome is a false alarm:
/// 1. The router gives its WAN byte counters (UPnP): a spike of three seconds or more while the OTHER devices moved at
///    least 20 Mbps and this PC was quiet names the cause, with the numbers ("thiết bị khác dùng 780 Mbps").
/// 2. No counters (the stock Viettel / VNPT modems measured): the router held a queue - the floor of its own ping rose -
///    while this PC was quiet, so another device was moving data through it (a weak signal alone never raises the floor,
///    measured 2026-10-20). Says "another device" but never which one: from this PC it cannot be told.
///
/// The card follows what is happening, not a timer: the owner's first match (2026-10-20) kept a red card long after the
/// phone's speed test had stopped. A warning holds while the load that caused it is still measured (other devices still
/// over 20 Mbps), or for <see cref="HoldFor"/> after the spike when there is no live figure; then the card goes back to
/// good and keeps one "Gần nhất" line for <see cref="RememberFor"/>, so a player who looks after the match still sees it.
/// Advice only, no button, nothing changed.
/// </summary>
public sealed class HomeLineCheck : CheckItem
{
    private static readonly Geometry HouseIcon = Geometry.Parse("M2,8 L8,2 L14,8 M4,6.5 V14 H12 V6.5 M6.5,14 V10 H9.5 V14");

    /// <summary>A spike this recent is still a warning, whatever the live figure says.</summary>
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(60);

    /// <summary>How long a load that is still going keeps the warning after its spike.</summary>
    private static readonly TimeSpan LoadHoldsFor = TimeSpan.FromMinutes(10);

    /// <summary>How long the last spike stays as a line on the good card.</summary>
    private static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(30);

    private const double LoadMbps = 20;

    public override string Id => "homeline";
    public override Geometry Icon => HouseIcon;
    public override string Title => T("check.home.title");

    private StatusMessage? _status;

    public override void OnStatus(StatusMessage status)
    {
        _status = status;
        Evaluate();
    }

    public override void OnServiceGone()
    {
        _status = null;
        Evaluate();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Evaluate();
    }

    private void Evaluate()
    {
        var status = _status;
        var line = status?.HomeLine;
        var now = DateTimeOffset.UtcNow;

        if (line is null)
        {
            // Measured from Connect on; the first answers take a few seconds.
            Severity = CheckSeverity.Unknown;
            Summary = T(Connected(status) ? "check.home.measuring" : "check.home.pending");
            Lines = [];
            return;
        }

        var household = line.HouseholdAt is { } h ? Entry(line, h) : null;
        var congested = line.CongestedAt is { } c ? CongestedEntry(line, c) : null;
        var busy = line.BusyAt is { } b ? BusyEntry(line, b) : null;

        if (HouseholdNow(line, now))
        {
            Severity = status!.GameRunning ? CheckSeverity.Critical : CheckSeverity.Warning;
            Summary = T("check.home.household");
            var lines = new List<DetailLine> { new(household!, IsEntry: true) };
            if (line.OtherMbpsNow is { } nowMbps) lines.Add(new DetailLine(Loc.F("check.home.otherNow", Whole(nowMbps)), IsEntry: true));
            if (line.HouseholdCount > 1) lines.Add(new DetailLine(Loc.F("check.home.times", line.HouseholdCount), IsEntry: true));
            lines.Add(new DetailLine(T("check.home.householdAdvice")));
            Lines = lines;
            return;
        }

        if (BusyNow(line, now))
        {
            Severity = CheckSeverity.Warning;
            var named = line.BusyOtherMbps >= LoadMbps;
            Summary = T(named ? "check.home.household" : "check.home.congested");
            var lines = new List<DetailLine> { new(busy!, IsEntry: true) };
            if (named && !line.BusyNow && line.OtherMbpsNow is { } nowMbps)
            {
                lines.Add(new DetailLine(Loc.F("check.home.otherNow", Whole(nowMbps)), IsEntry: true));
            }
            if (line.BusyCount > 1) lines.Add(new DetailLine(Loc.F("check.home.times", line.BusyCount), IsEntry: true));
            lines.Add(new DetailLine(T(named ? "check.home.householdAdvice" : "check.home.congestedAdvice")));
            if (line.RouterCounters == false) lines.Add(new DetailLine(T("check.home.noCounters")));
            Lines = lines;
            return;
        }

        if (CongestedNow(line, now))
        {
            Severity = CheckSeverity.Warning;
            Summary = T("check.home.congested");
            var lines = new List<DetailLine> { new(congested!, IsEntry: true) };
            if (line.CongestedCount > 1) lines.Add(new DetailLine(Loc.F("check.home.times", line.CongestedCount), IsEntry: true));
            lines.Add(new DetailLine(T("check.home.congestedAdvice")));
            if (line.RouterCounters == false) lines.Add(new DetailLine(T("check.home.noCounters")));
            Lines = lines;
            return;
        }

        var history = new List<DetailLine>();
        // The latest of the three, as history: it is over, so it is a line, not a warning.
        var latest = new[] { (At: line.HouseholdAt, Text: household), (At: line.CongestedAt, Text: congested), (At: line.BusyAt, Text: busy) }
            .Where(e => e.At is not null && e.Text is not null)
            .OrderByDescending(e => e.At)
            .FirstOrDefault();
        if (latest is { At: { } at, Text: { } text } && Within(at, now, RememberFor))
        {
            history.Add(new DetailLine(Loc.F("check.home.last", text), IsEntry: true));
        }
        if (line.OtherMbpsNow is { } otherNow) history.Add(new DetailLine(Loc.F("check.home.otherLive", Whole(otherNow)), IsEntry: true));
        if (line.RouterCounters is { } counters) history.Add(new DetailLine(T(counters ? "check.home.counters" : "check.home.noCounters")));

        if (line.RouterPings == false)
        {
            // Nothing live to judge on: the router ignores pings. Counters, if any, still name a load in a match.
            Severity = CheckSeverity.Unknown;
            Summary = T("check.home.noPing");
            Lines = history;
            return;
        }
        if (line.RouterPings is null && latest.At is null)
        {
            Severity = CheckSeverity.Unknown;
            Summary = T(Connected(status) ? "check.home.measuring" : "check.home.pending");
            Lines = history;
            return;
        }

        Severity = CheckSeverity.Good;
        Summary = T("check.home.good");
        Lines = history;
    }

    private static bool Connected(StatusMessage? status) =>
        status is { State: TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting };

    /// <summary>
    /// Whether this card is warning right now: the router busy with another device at home. <see cref="NetworkCheck"/>
    /// reads it too, so the jitter to the router that the same queue causes is not also blamed on the Wi-Fi.
    /// </summary>
    internal static bool RouterBusy(HomeLineStatus? line, DateTimeOffset now) =>
        line is not null && (HouseholdNow(line, now) || BusyNow(line, now) || CongestedNow(line, now));

    /// <summary>
    /// The live watch: busy now, or within <see cref="HoldFor"/> of it ending, or - when it was another device by the
    /// router's counters - while those devices still move as much, up to <see cref="LoadHoldsFor"/>.
    /// </summary>
    private static bool BusyNow(HomeLineStatus line, DateTimeOffset now) =>
        line.BusyNow ||
        line.BusyAt is { } at &&
        (Within(at, now, HoldFor) || line.BusyOtherMbps >= LoadMbps && line.OtherMbpsNow >= LoadMbps && Within(at, now, LoadHoldsFor));

    private static bool HouseholdNow(HomeLineStatus line, DateTimeOffset now) =>
        line.HouseholdAt is { } at &&
        (Within(at, now, HoldFor) || line.OtherMbpsNow >= LoadMbps && Within(at, now, LoadHoldsFor));

    private static bool CongestedNow(HomeLineStatus line, DateTimeOffset now) =>
        line.CongestedAt is { } at && Within(at, now, HoldFor);

    private static string Entry(HomeLineStatus line, long at) => line.HouseholdOtherMbps is { } mbps
        ? Loc.F("check.home.householdEntry", Clock(at), Whole(line.HouseholdExcessMs), Whole(line.HouseholdSeconds), Whole(mbps))
        : Loc.F("check.home.householdEntryNoMbps", Clock(at), Whole(line.HouseholdExcessMs), Whole(line.HouseholdSeconds));

    private static string CongestedEntry(HomeLineStatus line, long at) =>
        Loc.F("check.home.congestedEntry", Clock(at), Whole(line.CongestedGatewayMs), Whole(line.CongestedSeconds));

    /// <summary>The live watch's stretch: going on ("since HH:mm") or over ("HH:mm ... for Y s").</summary>
    private static string BusyEntry(HomeLineStatus line, long at)
    {
        if (line.BusyNow)
        {
            var since = at - (long)Math.Round(line.BusySeconds ?? 0);
            // While it lasts, the live figure when there is one: the median so far lags a load that just grew.
            return (line.BusyOtherMbps is not null ? line.OtherMbpsNow ?? line.BusyOtherMbps : null) is { } now
                ? Loc.F("check.home.busyNowMbps", Clock(since), Whole(line.BusyGatewayMs), Whole(now))
                : Loc.F("check.home.busyNowEntry", Clock(since), Whole(line.BusyGatewayMs));
        }
        return line.BusyOtherMbps is { } mbps
            ? Loc.F("check.home.busyEntryMbps", Clock(at), Whole(line.BusyGatewayMs), Whole(line.BusySeconds), Whole(mbps))
            : Loc.F("check.home.congestedEntry", Clock(at), Whole(line.BusyGatewayMs), Whole(line.BusySeconds));
    }

    private static bool Within(long unixSeconds, DateTimeOffset now, TimeSpan within) =>
        now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds) <= within;

    private static string Clock(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Whole(double? value) => Math.Round(value ?? 0).ToString("0", CultureInfo.InvariantCulture);
}
