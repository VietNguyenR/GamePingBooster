using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.Core.Ipc;
using GamePingBooster.App.Services.Localization;

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

        // Only the reply to OUR command. Statuses arrive continuously - a heartbeat every
        // second, plus every state change - and "the next one to arrive" is almost never the
        // answer to the save. Reading whichever landed first is how this screen came to report
        // a failed CONNECT as a failed save: the tunnel's Error is sticky after a rejected
        // handshake, so every heartbeat carried it and the save, already written to disk, was
        // announced as broken.
        if (status.AckVerb != "set-relay") return;

        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            _awaitingReply = false;

            // CommandError, not Error. This one is about the message just sent; Error is about
            // the tunnel, and a relay that is unreachable, in a different auth mode, or refusing
            // the key it holds has no bearing on whether settings were saved. Connect is what
            // asks the network anything - and it still reports all of that, in the main window.
            if (!string.IsNullOrWhiteSpace(status.CommandError))
            {
                vm.Error = status.CommandError;
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
            vm.Error = Loc.T("settings.noService");
            return;
        }

        try
        {
            // Its own verb, sent first and not awaited for a verdict: it is a single switch the
            // service saves on its own, and the relay save below still decides whether this window
            // reports success.
            if (vm.QualitySharingChanged)
            {
                await _pipe.SendAsync(new CommandMessage { Verb = "set-quality-sharing", Enabled = vm.QualitySharing });
            }

            // Nothing for the service to write, so nothing to send and nothing to wait for. This
            // screen also carries the language, which this app saves by itself the moment it is
            // picked - and sending set-relay for that made the service reload its profile, which
            // fails on a machine that has not signed in yet. A save that changed only the
            // language was reported as a failure because of a file it never needed.
            if (!vm.RelaySettingsChanged)
            {
                vm.Saved = true;
                Close();
                return;
            }

            // Set before the send, never after: the reply can arrive while SendAsync is still
            // returning, and a flag set afterwards would miss it and leave the window open.
            _awaitingReply = true;
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
            vm.Error = Loc.F("settings.serviceUnreachable", ex.Message);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
