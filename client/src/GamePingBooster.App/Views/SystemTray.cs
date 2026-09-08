using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using GamePingBooster.App.ViewModels;

namespace GamePingBooster.App.Views;

/// <summary>
/// The notification-area icon: where the window goes when it is minimised, and the only way back
/// to it once it is there.
///
/// Minimising HIDES the window rather than shrinking it to the taskbar - see
/// MainWindow.EnableMinimizeToTray. That behaviour is switched on from here, after the icon has
/// actually been created, and stays off if creating it throws: a window that hides itself with
/// no icon to click is a window with no way back, and behind it a tunnel with no way to stop it.
/// A missing tray icon costs a nicety; a missing tray icon plus a vanishing window costs the
/// user their routing table.
///
/// Deliberately not the app's exit condition. The window is hidden, not closed, so Avalonia's
/// default ShutdownMode (OnLastWindowClose) is unaffected: hiding does not remove a window from
/// the lifetime's list, only closing does. Nothing here needs OnExplicitShutdown, which would
/// mean a closed window leaving the process running with nothing on screen.
/// </summary>
public sealed class SystemTray : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly MainWindow _window;
    private readonly MainViewModel _vm;

    /// <param name="exit">
    /// The app's full exit: tunnel down first, then shutdown. Not desktop.Shutdown(), which is a
    /// forced shutdown and does NOT raise ShutdownRequested - wiring Exit to it would end the
    /// process with the adapter and every game route still in place.
    /// </param>
    public SystemTray(MainWindow window, MainViewModel vm, Action exit)
    {
        _window = window;
        _vm = vm;

        // The icon first, then the menu that closes over it: an Exit handler written before the
        // field is assigned is a handler the compiler cannot prove is safe.
        _icon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = ToolTipText(),
        };

        var show = new NativeMenuItem("Show Game Ping Booster");
        show.Click += (_, _) => _window.RestoreFromTray();

        var quit = new NativeMenuItem("Exit");
        quit.Click += (_, _) =>
        {
            // Gone the moment it is pressed. Teardown takes seconds on a bad day and there is no
            // window on screen to say so, so an icon that sits there reads as nothing happened.
            _icon.IsVisible = false;
            exit();
        };

        var menu = new NativeMenu();
        menu.Add(show);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);
        _icon.Menu = menu;

        // Left click: what every other icon in the notification area answers to. Restores rather
        // than toggles - "click it and it comes back" needs no explaining, and a toggle would
        // hide the window for anyone who clicks the icon to check the app is still there.
        _icon.Clicked += (_, _) => _window.RestoreFromTray();

        // Raised on the UI thread already: every update in MainViewModel goes through
        // Dispatcher.UIThread.Post. See MainViewModel.OnStatus.
        _vm.PropertyChanged += OnViewModelChanged;

        _window.EnableMinimizeToTray();
    }

    /// <summary>
    /// The app icon, from the compiled-in resource rather than a file beside the exe: the tray
    /// icon has to survive an installation the user has tidied, and AvaloniaResource puts it
    /// inside the binary.
    /// </summary>
    private static WindowIcon LoadIcon() =>
        new(AssetLoader.Open(new Uri("avares://GamePingBooster/favicon.ico")));

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The two the tooltip is made of. Both are raised by the properties they derive from.
        if (e.PropertyName is not (null
            or nameof(MainViewModel.StatusText)
            or nameof(MainViewModel.GamePingText))) return;

        _icon.ToolTipText = ToolTipText();
    }

    /// <summary>
    /// What hovering the icon says. Short on purpose: Windows truncates a tray tooltip, and the
    /// one thing worth reading without opening the window is whether it is connected and at what
    /// ping.
    /// </summary>
    private string ToolTipText() => _vm.GamePingMs is not null
        ? $"Game Ping Booster - {_vm.StatusText}, {_vm.GamePingText}"
        : $"Game Ping Booster - {_vm.StatusText}";

    /// <summary>
    /// Takes the icon out of the notification area. Without it Windows leaves a dead icon behind
    /// until something makes the shell notice the process is gone - usually the user hovering it.
    /// </summary>
    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
