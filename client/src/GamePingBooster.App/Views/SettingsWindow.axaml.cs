using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Views;

public partial class SettingsWindow : SurfaceWindow
{
    private PipeClient? _pipe;
    private bool _awaitingReply;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Listens on the same pipe as the main window.
    ///
    /// One reader raises the event to every handler, so a second subscriber costs nothing and
    /// races with nothing - and without it this window would have to report success the moment
    /// it sent the message, which would mean claiming a save that the service may well have
    /// rejected.
    /// </summary>
    public void Attach(PipeClient pipe)
    {
        _pipe = pipe;
        pipe.StatusReceived += OnStatus;
        Closed += (_, _) => pipe.StatusReceived -= OnStatus;
    }

    private void OnStatus(StatusMessage status)
    {
        if (!_awaitingReply) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            _awaitingReply = false;

            if (!string.IsNullOrWhiteSpace(status.Error))
            {
                vm.Error = status.Error;
                vm.Saved = false;
                return;
            }
            vm.Error = null;
            vm.Saved = true;

            // Close on success. The banner was there so the user could see it worked, but a
            // dialog that stays open after doing its job reads as one that did not.
            Close();
        });
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || _pipe is null) return;

        vm.Error = null;
        vm.Saved = false;

        if (!_pipe.IsConnected)
        {
            vm.Error = "The service is not running, so there is nothing to save to. " +
                       "Start it and try again.";
            return;
        }

        _awaitingReply = true;
        try
        {
            await _pipe.SendAsync(new CommandMessage
            {
                Verb = "set-relay",
                RelayEndpoints = vm.EndpointList,
                // Blank means "keep the key already stored", which the service understands. Send
                // it as null rather than an empty string so the intent is unambiguous on the
                // other side.
                Psk = string.IsNullOrWhiteSpace(vm.Psk) ? null : vm.Psk,
                // Sent as a string every time, never null: the box's contents ARE the intent, so
                // an emptied box has to clear the setting. Null is reserved for callers that
                // mean "leave it alone", and this screen never means that - it shows the current
                // value, so whatever is in it is what the user decided.
                LicenceUrl = vm.LicenceUrl.Trim(),
            });
        }
        catch (Exception ex)
        {
            _awaitingReply = false;
            vm.Error = $"Could not reach the service: {ex.Message}";
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
