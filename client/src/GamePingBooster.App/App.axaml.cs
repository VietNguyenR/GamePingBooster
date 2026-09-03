using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.App.Views;

namespace GamePingBooster.App;

public partial class App : Application
{
    private PipeClient? _pipe;
    private TokenRefresher? _refresher;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _pipe = new PipeClient();
            var vm = new MainViewModel(_pipe);

            // The window needs the pipe as well as the view model: the settings screen sends
            // on it directly, and listens on it for the service's verdict.
            var window = new MainWindow { DataContext = vm };
            window.Attach(_pipe);
            desktop.MainWindow = window;
            // Renews the licence token on its own, at half of its remaining life. It reads what
            // it needs from the view model rather than holding its own copy, so there is one
            // answer to "what does this client believe" and it is the one on screen.
            //
            // Harmless on a self-hosted installation: with no licence URL and no refresh token
            // it never sends anything, it just sleeps.
            _refresher = new TokenRefresher(
                _pipe,
                () => vm.LicenceUrl,
                () => vm.DevicePublicKey,
                // Marshalled: the refresher reports from its own loop, and raising
                // PropertyChanged off the UI thread breaks Avalonia's bindings in ways that
                // surface much later and somewhere else.
                message => Dispatcher.UIThread.Post(() => vm.LicenceNotice = message));
            _pipe.StatusReceived += _refresher.OnStatus;

            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_refresher is not null) await _refresher.DisposeAsync();
                if (_pipe is not null) await _pipe.DisposeAsync();
            };

            // Start listening to the service. Bringing the tunnel up waits for the user to press
            // the button - the app never rearranges the machine's routing on its own at startup.
            _pipe.Start();
            _refresher.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
