using System.Globalization;
using Avalonia.Media;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item A "Mạng trong nhà": another device at home filling the line while the player plays - the one cause item 3 cannot
/// see, because item 3 only sees this PC. Read from the spike recorder (StatusMessage.HomeLine), which judges each spike.
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
            // Measured only while a match runs: nothing to say before the first one.
            Severity = CheckSeverity.Unknown;
            Summary = T("check.home.pending");
            Lines = [];
            return;
        }

        var household = line.HouseholdAt is { } h ? Entry(line, h) : null;
        var congested = line.CongestedAt is { } c ? CongestedEntry(line, c) : null;

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

        Severity = CheckSeverity.Good;
        Summary = T("check.home.good");
        var good = new List<DetailLine>();
        // The latest of the two, as history: it is over, so it is a line, not a warning.
        var latest = (line.HouseholdAt ?? 0) >= (line.CongestedAt ?? 0)
            ? (At: line.HouseholdAt, Text: household)
            : (At: line.CongestedAt, Text: congested);
        if (latest is { At: { } at, Text: { } text } && Within(at, now, RememberFor))
        {
            good.Add(new DetailLine(Loc.F("check.home.last", text), IsEntry: true));
        }
        if (line.RouterCounters is { } counters) good.Add(new DetailLine(T(counters ? "check.home.counters" : "check.home.noCounters")));
        Lines = good;
    }

    /// <summary>
    /// Whether this card is warning right now: the router busy with another device at home. <see cref="NetworkCheck"/>
    /// reads it too, so the jitter to the router that the same queue causes is not also blamed on the Wi-Fi.
    /// </summary>
    internal static bool RouterBusy(HomeLineStatus? line, DateTimeOffset now) =>
        line is not null && (HouseholdNow(line, now) || CongestedNow(line, now));

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

    private static bool Within(long unixSeconds, DateTimeOffset now, TimeSpan within) =>
        now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds) <= within;

    private static string Clock(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Whole(double? value) => Math.Round(value ?? 0).ToString("0", CultureInfo.InvariantCulture);
}
