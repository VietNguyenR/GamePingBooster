using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>How an item on the "Kiểm tra máy" panel reads. The order is the panel's order: worst first.</summary>
public enum CheckSeverity
{
    Critical,
    Warning,
    /// <summary>Not measured yet - before Connect, or while the first measurement runs. Never counted as a problem.</summary>
    Unknown,
    Good,
}

/// <summary>
/// One line of the "Kiểm tra máy" panel: what it checks, how bad it is, and - when the app can do something about it -
/// a fix and the way back (pc-checklist-handoff.md, approved 2026-10-17).
///
/// Each item is a self-contained unit so that more can be added without touching the panel: it measures on its own,
/// says what it found through <see cref="Severity"/> and <see cref="Summary"/>, and raises PropertyChanged; the panel
/// only sorts and counts. Items that read the service's status override <see cref="OnStatus"/>; items that measure on
/// their own run their own timers.
///
/// The rules every item keeps (agreed with the owner): real numbers only, never "+x% ping"; every change has a
/// restore, and a temporary one undoes itself when the game closes; Windows only, never the game process.
/// </summary>
public abstract class CheckItem : INotifyPropertyChanged
{
    /// <summary>Stable id, for logs and for anything remembered per item.</summary>
    public abstract string Id { get; }

    /// <summary>The item's own icon, 16x16, drawn as a stroke. Also stands for it on the collapsed strip.</summary>
    public abstract Geometry Icon { get; }

    /// <summary>The item's name, in the UI language.</summary>
    public abstract string Title { get; }

    private CheckSeverity _severity = CheckSeverity.Unknown;
    public CheckSeverity Severity
    {
        get => _severity;
        protected set
        {
            if (!Set(ref _severity, value)) return;
            Raise(nameof(SeverityIcon));
            Raise(nameof(SeverityBrush));
            Raise(nameof(IsProblem));
        }
    }

    /// <summary>A warning or worse: counted in the header and the badge, listed above the fold.</summary>
    public bool IsProblem => Severity is CheckSeverity.Critical or CheckSeverity.Warning;

    private string _summary = "";
    /// <summary>One short line: what was found, with the number when there is one.</summary>
    public string Summary
    {
        get => _summary;
        protected set => Set(ref _summary, value);
    }

    private string? _detail;
    /// <summary>What to do about it, when the player has to do it. Shown under the summary on a problem only.</summary>
    public string? Detail
    {
        get => _detail;
        protected set
        {
            if (Set(ref _detail, value)) Raise(nameof(HasDetail));
        }
    }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    private IReadOnlyList<DetailLine> _lines = [];

    /// <summary>
    /// Detail as separate lines, for an item that needs a button on some of them (an app to skip). Shown where
    /// <see cref="Detail"/> is; an item uses one or the other.
    /// </summary>
    public IReadOnlyList<DetailLine> Lines
    {
        get => _lines;
        protected set
        {
            // Items re-evaluate on every status (once a second) and build new lines each time. Swapping in an equal
            // list would recreate every button under the mouse, and a click whose press and release straddle the swap
            // is lost (found 2026-10-17: "Chắc chắn?" missed most presses). Same text and same buttons: keep the old.
            if (SameLines(_lines, value)) return;
            if (Set(ref _lines, value)) Raise(nameof(HasLines));
        }
    }

    private static bool SameLines(IReadOnlyList<DetailLine> a, IReadOnlyList<DetailLine> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Text != b[i].Text || a[i].IsEntry != b[i].IsEntry) return false;
            var x = a[i].AllButtons;
            var y = b[i].AllButtons;
            if (x.Count != y.Count) return false;
            for (var j = 0; j < x.Count; j++)
                if (x[j].Text != y[j].Text) return false;
        }
        return true;
    }

    public bool HasLines => Lines.Count > 0;

    private bool _isExpanded;

    /// <summary>
    /// A good line opened by a click to show its whole summary (and detail): the panel is too narrow for "Wi-Fi 5 GHz ✓ ·
    /// sóng 81% · ..." on one line, and a tooltip was not enough (owner, 2026-10-17). Not remembered.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>The fix button's text, or null when the app has nothing to press for this item.</summary>
    public virtual string? FixLabel => null;

    public bool HasFix => FixLabel is not null && CanFix;

    /// <summary>There is a button to show at all - the row under the summary is left out otherwise.</summary>
    public bool HasActions => HasFix || CanRestore;

    /// <summary>Whether the fix can run right now.</summary>
    public virtual bool CanFix => false;

    /// <summary>Applies the fix. Only called while <see cref="HasFix"/>.</summary>
    public virtual Task FixAsync() => Task.CompletedTask;

    /// <summary>Something this item changed is still changed and can be put back.</summary>
    public virtual bool CanRestore => false;

    /// <summary>Puts back what <see cref="FixAsync"/> changed. Safe to call when nothing was.</summary>
    public virtual Task RestoreAsync() => Task.CompletedTask;

    /// <summary>A status from the service. Most items that measure on their own ignore it.</summary>
    public virtual void OnStatus(StatusMessage status)
    {
    }

    /// <summary>The service's pipe went away: whatever this item learned from it is no longer known.</summary>
    public virtual void OnServiceGone()
    {
    }

    /// <summary>The UI language changed: say everything again. The base re-raises what it owns.</summary>
    public virtual void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(FixLabel));
        Raise(nameof(HasFix));
        Raise(nameof(HasActions));
    }

    /// <summary>Tells the panel the fix or restore buttons may have changed.</summary>
    protected void RaiseActions()
    {
        Raise(nameof(FixLabel));
        Raise(nameof(CanFix));
        Raise(nameof(HasFix));
        Raise(nameof(CanRestore));
        Raise(nameof(HasActions));
    }

    // ------------------------------------------------------------ severity drawing

    private static readonly Geometry GoodIcon = Geometry.Parse("M3,8.5 L6.5,12 L13,4.5");
    private static readonly Geometry WarningIcon = Geometry.Parse("M8,1.5 L15,14.5 H1 Z M8,6 V10 M8,12 V12.6");
    private static readonly Geometry CriticalIcon = Geometry.Parse("M8,1 A7,7 0 1 1 7.99,1 Z M5.2,5.2 L10.8,10.8 M10.8,5.2 L5.2,10.8");
    private static readonly Geometry UnknownIcon = Geometry.Parse("M8,1 A7,7 0 1 1 7.99,1 Z");

    internal static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#4ade80"));
    internal static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#fbbf24"));
    internal static readonly IBrush CriticalBrush = new SolidColorBrush(Color.Parse("#f87171"));
    internal static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#787878"));

    public Geometry SeverityIcon => Severity switch
    {
        CheckSeverity.Good => GoodIcon,
        CheckSeverity.Warning => WarningIcon,
        CheckSeverity.Critical => CriticalIcon,
        _ => UnknownIcon,
    };

    public IBrush SeverityBrush => Severity switch
    {
        CheckSeverity.Good => GoodBrush,
        CheckSeverity.Warning => WarningBrush,
        CheckSeverity.Critical => CriticalBrush,
        _ => UnknownBrush,
    };

    protected static string T(string key) => Loc.T(key);

    // ------------------------------------------------------------ paid items

    /// <summary>
    /// The account has no plan (the licence server said 402, or the plan ran out mid-session) - Connect will be refused.
    /// Items 2-5 and 7 are free and do not read this; the ones measured or fixed through the tunnel (DNS, Mạng trong
    /// nhà) say they come with a plan instead of "press Start boosting" (owner, 2026-10-22).
    /// </summary>
    protected bool NeedsPlan { get; private set; }

    /// <summary>Opens the plans window. Set by the panel; null until the main window is up.</summary>
    internal Action? OpenPlans { get; set; }

    internal void SetNeedsPlan(bool value)
    {
        if (NeedsPlan == value) return;
        NeedsPlan = value;
        OnPlanChanged();
    }

    /// <summary>Re-evaluate when <see cref="NeedsPlan"/> changes.</summary>
    protected virtual void OnPlanChanged()
    {
    }

    /// <summary>The line under a paid item for an account with no plan: what it does, and the plans button.</summary>
    protected DetailLine PlanLine(string key) =>
        new(T(key), Buttons: OpenPlans is { } open ? [new LineButton(T("check.plan.open"), open)] : null);

    // --------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One line of an item's detail. IsEntry marks a line about one thing (an app), drawn a little brighter and indented;
/// its buttons, if any, sit at its right.
/// </summary>
public sealed record DetailLine(string Text, bool IsEntry = false, IReadOnlyList<LineButton>? Buttons = null)
{
    public IReadOnlyList<LineButton> AllButtons => Buttons ?? [];
}

/// <summary>A link-style button on a detail line.</summary>
public sealed record LineButton(string Text, Action OnClick);
