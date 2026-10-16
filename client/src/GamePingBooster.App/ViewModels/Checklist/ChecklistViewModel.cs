using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// The "Kiểm tra máy" panel on the right of the main window (pc-checklist-handoff.md, approved 2026-10-17).
///
/// It only sorts and counts: every item measures and fixes on its own (<see cref="CheckItem"/>). Red items first, then
/// amber, then the ones not measured yet; the good ones fold into "Đang tốt (n)" at the bottom, closed by default, so
/// the list grows without the layout changing.
///
/// Open or collapsed is the player's choice and is remembered (<see cref="ChecklistStore"/>). A new problem never
/// reopens a collapsed panel - the window would jump under the mouse - it only moves the badge on the strip.
/// </summary>
public sealed class ChecklistViewModel : INotifyPropertyChanged
{
    /// <summary>Every item, in build order. The list on screen is sorted from this.</summary>
    private readonly List<CheckItem> _items;

    public ChecklistViewModel(PipeClient pipe)
    {
        _items =
        [
            new DnsCheck(),
            new NetworkCheck(),
        ];
        foreach (var item in _items) item.PropertyChanged += OnItemChanged;

        _isOpen = ChecklistStore.LoadOpen();

        pipe.StatusReceived += status => Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in _items) item.OnStatus(status);
        });
        pipe.Disconnected += _ => Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in _items) item.OnServiceGone();
        });
        Loc.Changed += OnLanguageChanged;

        Resort();
    }

    // ------------------------------------------------------------ open / collapsed

    private bool _isOpen;

    /// <summary>The whole panel is shown; false leaves the thin strip on the window's right edge.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value)) return;
            Raise(nameof(IsCollapsed));
            ChecklistStore.SaveOpen(value);
        }
    }

    public bool IsCollapsed => !IsOpen;

    public void Toggle() => IsOpen = !IsOpen;

    private bool _isGoodExpanded;

    /// <summary>"Đang tốt (n)" unfolded. Not remembered: closed is the point of folding them.</summary>
    public bool IsGoodExpanded
    {
        get => _isGoodExpanded;
        set
        {
            if (Set(ref _isGoodExpanded, value)) Raise(nameof(GoodChevron));
        }
    }

    public void ToggleGood() => IsGoodExpanded = !IsGoodExpanded;

    private static readonly Geometry ChevronDown = Geometry.Parse("M1,1 L5,5 L9,1");
    private static readonly Geometry ChevronRight = Geometry.Parse("M1,1 L5,5 L1,9");

    public Geometry GoodChevron => IsGoodExpanded ? ChevronDown : ChevronRight;

    // ------------------------------------------------------------ lists

    /// <summary>Problems and items not measured yet, worst first.</summary>
    public ObservableCollection<CheckItem> Attention { get; } = [];

    /// <summary>The items that are fine, under the fold.</summary>
    public ObservableCollection<CheckItem> Good { get; } = [];

    /// <summary>The problems alone, for the icons on the collapsed strip.</summary>
    public ObservableCollection<CheckItem> Problems { get; } = [];

    public int ProblemCount => Problems.Count;

    public bool HasProblems => ProblemCount > 0;

    public bool HasGood => Good.Count > 0;

    public string GoodHeader => Loc.F("check.panel.good", Good.Count);

    public string HeaderText => ProblemCount > 0
        ? Loc.F("check.panel.attention", ProblemCount)
        : _items.All(i => i.Severity == CheckSeverity.Good)
            ? T("check.panel.allGood")
            : "";

    public bool HasHeaderText => HeaderText.Length > 0;

    /// <summary>Red when anything is red, amber for warnings, green when all is well.</summary>
    public IBrush HeaderBrush =>
        _items.Any(i => i.Severity == CheckSeverity.Critical) ? CheckItem.CriticalBrush
        : ProblemCount > 0 ? CheckItem.WarningBrush
        : CheckItem.GoodBrush;

    public string BadgeText => ProblemCount > 9 ? "9+" : ProblemCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool CanRestoreAll => _items.Any(i => i.CanRestore);

    /// <summary>Puts back everything any item changed. Each item's own restore is safe when it changed nothing.</summary>
    public async Task RestoreAllAsync()
    {
        foreach (var item in _items.Where(i => i.CanRestore).ToList())
        {
            try
            {
                await item.RestoreAsync();
            }
            catch (Exception)
            {
                // One item failing to restore must not keep the others changed. The item says what is left.
            }
        }
        Raise(nameof(CanRestoreAll));
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CheckItem.Severity)) Resort();
        if (e.PropertyName is nameof(CheckItem.CanRestore)) Raise(nameof(CanRestoreAll));
    }

    private void Resort()
    {
        // Stable within a severity: build order, so lines do not swap places when nothing about them changed.
        var attention = _items.Where(i => i.Severity != CheckSeverity.Good).OrderBy(i => i.Severity).ToList();
        var good = _items.Where(i => i.Severity == CheckSeverity.Good).ToList();

        Replace(Attention, attention);
        Replace(Good, good);
        Replace(Problems, attention.Where(i => i.IsProblem).ToList());

        Raise(nameof(ProblemCount));
        Raise(nameof(HasProblems));
        Raise(nameof(HasGood));
        Raise(nameof(GoodHeader));
        Raise(nameof(HeaderText));
        Raise(nameof(HasHeaderText));
        Raise(nameof(BadgeText));
        Raise(nameof(HeaderBrush));
    }

    /// <summary>Only touches the collection when the order really changed, so an open list does not flicker every status.</summary>
    private static void Replace(ObservableCollection<CheckItem> target, List<CheckItem> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void OnLanguageChanged()
    {
        foreach (var item in _items) item.OnLanguageChanged();
        Raise(nameof(GoodHeader));
        Raise(nameof(HeaderText));
        Raise(nameof(HasHeaderText));
    }

    private static string T(string key) => Loc.T(key);

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
