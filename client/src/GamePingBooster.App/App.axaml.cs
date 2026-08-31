using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.App.Views;

namespace GamePingBooster.App;

public partial class App : Application
{
    private PipeClient? _pipe;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _pipe = new PipeClient();
            var vm = new MainViewModel(_pipe);

            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_pipe is not null) await _pipe.DisposeAsync();
            };

            // Start listening to the service. Bringing the tunnel up waits for the user to press
            // the button - the app never rearranges the machine's routing on its own at startup.
            _pipe.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
