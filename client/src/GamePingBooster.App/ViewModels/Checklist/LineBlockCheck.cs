using Avalonia.Media;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// The line breaking a game or Steam (block-detect-handoff.md, agreed with the owner 2026-10-22): free to see, paid to fix.
/// The service measures it while nothing is connected (LineBlockWatch) and sends the services it found in
/// <see cref="StatusMessage.LineBlocks"/>.
///
/// Hidden unless something is broken, by the owner's decision: "your line is fine" would only tell a player they no
/// longer need the app, and one clean reading from one machine proves less than it says.
/// </summary>
public sealed class LineBlockCheck : CheckItem
{
    /// <summary>Two plugs pulled apart.</summary>
    private static readonly Geometry LineIcon =
        Geometry.Parse("M1,8 H5 M5,5 V11 M5,5 H7 V11 H5 M15,8 H11 M11,5 V11 M11,5 H9 V11 H11");

    public LineBlockCheck() => Hidden = true;

    public override string Id => "line";
    public override Geometry Icon => LineIcon;
    public override string Title => T("check.line.title");

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

    protected override void OnPlanChanged() => Evaluate();

    private void Evaluate()
    {
        var blocks = _status?.LineBlocks ?? [];
        if (blocks.Count == 0)
        {
            Hidden = true;
            Severity = CheckSeverity.Unknown;
            return;
        }

        Hidden = false;
        var services = string.Join(", ", blocks);
        var session = _status is { State: TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting };

        // Connected, the app's fix is on: still said, so the player sees what the plan is doing for them.
        if (session)
        {
            Severity = _status is { Unblock: true } ? CheckSeverity.Good : CheckSeverity.Unknown;
            Summary = _status is { Unblock: true }
                ? Loc.F("check.line.handled", services)
                : Loc.F("check.line.blocked", services);
            Detail = null;
            Lines = [];
            return;
        }

        Severity = CheckSeverity.Warning;
        Summary = Loc.F("check.line.blocked", services);
        if (NeedsPlan)
        {
            Detail = null;
            Lines = [PlanLine("check.line.plan")];
        }
        else
        {
            Lines = [];
            Detail = T("check.line.connect");
        }
    }
}
