using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Views;

public partial class GamesWindow : SurfaceWindow
{
    private PipeClient? _pipe;

    /// <summary>The game the last heartbeat said was running, so a change can ask for the list again.</summary>
    private string? _runningGame;

    public GamesWindow()
    {
        InitializeComponent();
        DataContext = new GamesViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Asks the service for the list and listens for the answer on the shared pipe - see
    /// SettingsWindow.Attach for why a second subscriber is the right way to hear a reply.
    /// </summary>
    public void Attach(PipeClient pipe, string? runningGame)
    {
        _pipe = pipe;
        _runningGame = runningGame;
        pipe.StatusReceived += OnStatus;
        Closed += (_, _) => pipe.StatusReceived -= OnStatus;
        Opened += (_, _) => _ = RequestAsync();
    }

    private void OnStatus(StatusMessage status)
    {
        if (status.AckVerb == "games")
        {
            var games = status.Games ?? [];
            Dispatcher.UIThread.Post(() => (DataContext as GamesViewModel)?.Load(games));
            return;
        }

        // A heartbeat. Only a game opening or closing changes what the list shows, so only that
        // asks again - not every second.
        var running = status.GameRunning ? status.GameName : null;
        if (running == _runningGame) return;
        _runningGame = running;
        _ = RequestAsync();
    }

    private async Task RequestAsync()
    {
        if (_pipe is null) return;
        try
        {
            await _pipe.SendAsync(new CommandMessage { Verb = "games" }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The service is not reachable; the main window already says so. Show the empty state
            // rather than "Loading..." for ever.
            Dispatcher.UIThread.Post(() => (DataContext as GamesViewModel)?.Load([]));
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
