using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels;

/// <summary>One line of the "Supported games" window.</summary>
public sealed class GameRow
{
    public GameRow(SupportedGame game)
    {
        Name = game.Name;
        Processes = string.Join(", ", game.ProcessNames);
        Regions = string.Join(", ", game.Regions);
        Badge = game.Running ? "Running" : game.LastPlayed ? "Last played" : "";
        _search = string.Join('\n', [game.Name, game.Id, .. game.ProcessNames]);
    }

    public string Name { get; }

    /// <summary>What detection looks for. The first thing support asks when a game is not picked up.</summary>
    public string Processes { get; }

    public string Regions { get; }
    public bool HasRegions => Regions.Length > 0;

    public string Badge { get; }
    public bool HasBadge => Badge.Length > 0;

    private readonly string _search;

    /// <summary>Name, id or any process name - somebody may only know "TslGame.exe" from Task Manager.</summary>
    public bool Matches(string query) => _search.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>
/// The "Supported games" window: every game the profile carries, searchable.
///
/// The list lives here and not on the main window because it grows with every game added - the main
/// window says how many there are and which one is running, and this is where the names go.
/// </summary>
public sealed class GamesViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<GameRow> _all = [];

    private bool _loading = true;
    public bool Loading
    {
        get => _loading;
        private set { if (Set(ref _loading, value)) RaiseList(); }
    }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (Set(ref _query, value ?? "")) RaiseList(); }
    }

    public IReadOnlyList<GameRow> Visible
    {
        get
        {
            var query = Query.Trim();
            return query.Length == 0 ? _all : _all.Where(row => row.Matches(query)).ToList();
        }
    }

    public string CountText => _all.Count == 1 ? "1 game" : $"{_all.Count} games";

    /// <summary>Only there when the list is empty, and says which kind of empty.</summary>
    public string EmptyText => Loading ? "Loading..."
        : _all.Count == 0 ? "No game list yet. Sign in, or wait for the game list to download."
        : $"No game matches \"{Query.Trim()}\".";

    public bool IsEmpty => Visible.Count == 0;

    /// <summary>Only the search box, when there are few enough games to see at a glance, is noise.</summary>
    public bool ShowSearch => _all.Count > 6;

    public void Load(IEnumerable<SupportedGame> games)
    {
        _all = games.Select(g => new GameRow(g)).ToList();
        Loading = false;
        Raise(nameof(CountText));
        Raise(nameof(ShowSearch));
        RaiseList();
    }

    private void RaiseList()
    {
        Raise(nameof(Visible));
        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyText));
    }

    // ------------------------------------------------------------------ boilerplate

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
