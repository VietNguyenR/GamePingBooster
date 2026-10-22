using Avalonia.Media;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item 1: DNS. Nothing to measure and nothing to press - the app's own unblock resolver handles names, and this line
/// says so, or says why it cannot (Windows set to Require DoH, something else holding DNS). It reads the same fields
/// as the Sửa lỗi line on the main card, so the two never disagree.
///
/// The app does not change the system DNS server, on purpose: it has no effect on ping in a match, and it would fight
/// the unblock resolver (NRPT/DoH). So there is no "switch to 1.1.1.1" button here and there must not be one.
/// </summary>
public sealed class DnsCheck : CheckItem
{
    private static readonly Geometry DnsIcon =
        Geometry.Parse("M8,1 A7,7 0 1 1 7.99,1 Z M1,8 H15 M8,1 C4.5,4.5 4.5,11.5 8,15 M8,1 C11.5,4.5 11.5,11.5 8,15");

    public override string Id => "dns";
    public override Geometry Icon => DnsIcon;
    public override string Title => T("check.dns.title");

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
        var status = _status;
        var session = status is { State: TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting };

        if (status is { UnblockProblemCode: { } problem })
        {
            // Only the player can fix it; the service's language key carries the how-to.
            Severity = CheckSeverity.Warning;
            Summary = T("check.dns.problem");
            Detail = T(problem);
            Lines = [];
            return;
        }

        Detail = null;

        // Paid: the fix runs through Connect, which an account with no plan cannot press.
        if (NeedsPlan && !session)
        {
            Severity = CheckSeverity.Unknown;
            Summary = T("check.plan.summary");
            Lines = [PlanLine("check.dns.plan")];
            return;
        }
        Lines = [];

        if (status is { Unblock: true })
        {
            Severity = CheckSeverity.Good;
            Summary = T("check.dns.good");
            return;
        }

        // Off with no reason given. Unblocking runs while connected (unblock-needs-connect), so before Connect this is
        // simply not checked yet, and right after it the service is still turning it on. Never shown as a problem: a
        // service too old to report it sends nothing at all, and that is not the player's fault either.
        Severity = CheckSeverity.Unknown;
        Summary = session ? T("check.dns.starting") : T("check.pending");
    }
}
