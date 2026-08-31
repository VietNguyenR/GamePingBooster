using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// The app's only view model. Keeps things simple: one toggle button, one status line, a few
/// numbers. INotifyPropertyChanged is hand-written rather than pulling in an MVVM library - the
/// app has a single screen, not worth another dependency in a Native AOT binary.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PipeClient _pipe;

    public MainViewModel(PipeClient pipe)
    {
        _pipe = pipe;
        _pipe.StatusReceived += OnStatus;
        _pipe.Disconnected += OnDisconnected;
    }

    // ------------------------------------------------------------ displayed state

    private TunnelState _state = TunnelState.Disconnected;
    public TunnelState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
            Raise(nameof(ActionButtonText));
            Raise(nameof(IsBusy));
            Raise(nameof(CanPressAction));
        }
    }

    private string _detail = "Starting up...";
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    private string? _error;
    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    private double? _pingMs;
    public double? PingMs
    {
        get => _pingMs;
        private set
        {
            if (Set(ref _pingMs, value)) Raise(nameof(PingText));
        }
    }

    private double? _lossRatio;
    public double? LossRatio
    {
        get => _lossRatio;
        private set
        {
            if (Set(ref _lossRatio, value)) Raise(nameof(LossText));
        }
    }

    private bool _gameRunning;
    public bool GameRunning
    {
        get => _gameRunning;
        private set
        {
            if (Set(ref _gameRunning, value)) Raise(nameof(GameText));
        }
    }

    private string? _gameName;
    public string? GameName
    {
        get => _gameName;
        private set
        {
            if (Set(ref _gameName, value)) Raise(nameof(GameText));
        }
    }

    private string? _relayName;
    public string? RelayName
    {
        get => _relayName;
        private set
        {
            if (Set(ref _relayName, value)) Raise(nameof(RelayText));
        }
    }

    private int _activeRoutes;
    public int ActiveRoutes
    {
        get => _activeRoutes;
        private set
        {
            if (Set(ref _activeRoutes, value)) Raise(nameof(RouteText));
        }
    }

    private long _packetsSent;
    public long PacketsSent
    {
        get => _packetsSent;
        private set
        {
            if (Set(ref _packetsSent, value)) Raise(nameof(PacketsText));
        }
    }

    private long _packetsReceived;
    public long PacketsReceived
    {
        get => _packetsReceived;
        private set
        {
            if (Set(ref _packetsReceived, value)) Raise(nameof(PacketsText));
        }
    }

    // --------------------------------------------------------- derived UI properties

    public string StatusText => State switch
    {
        TunnelState.Disconnected => "Not connected",
        TunnelState.Connecting => "Connecting...",
        TunnelState.Connected => "Connected",
        TunnelState.Reconnecting => "Reconnecting...",
        TunnelState.Faulted => "Error",
        _ => "Unknown",
    };

    /// <summary>
    /// A ready-made brush instead of a colour string: binding a string to IBrush goes through a
    /// TypeConverter, and TypeConverters are exactly what the Native AOT trimmer removes.
    /// </summary>
    public IBrush StatusBrush => State switch
    {
        TunnelState.Connected => Brushes.LimeGreen,
        TunnelState.Connecting or TunnelState.Reconnecting => Brushes.Orange,
        TunnelState.Faulted => Brushes.OrangeRed,
        _ => Brushes.Gray,
    };

    public string ActionButtonText => State is TunnelState.Connected or TunnelState.Connecting
        ? "Disconnect"
        : "Connect";

    public bool IsBusy => State is TunnelState.Connecting or TunnelState.Reconnecting;
    public bool CanPressAction => !IsBusy;

    public string PingText => PingMs is { } p ? $"{p:F0} ms" : "-";
    public string LossText => LossRatio is { } l ? $"{l * 100:F1}%" : "-";
    public string RelayText => RelayName ?? "-";
    public string RouteText => ActiveRoutes > 0 ? $"{ActiveRoutes} ranges" : "-";

    public string GameText => GameName is null
        ? "-"
        : GameRunning ? $"{GameName} is running" : $"{GameName} is not open";

    /// <summary>
    /// Packet counters. Not cosmetic: when the tunnel connects but traffic does not flow, the
    /// first question is always whether the client is sending at all, and this answers it
    /// without attaching a packet capture.
    /// </summary>
    public string PacketsText => $"{PacketsSent} up / {PacketsReceived} down";

    // ------------------------------------------------------------------- actions

    public async Task ToggleAsync()
    {
        try
        {
            Error = null;
            if (State is TunnelState.Connected or TunnelState.Connecting)
            {
                await _pipe.DisconnectTunnelAsync().ConfigureAwait(false);
            }
            else
            {
                State = TunnelState.Connecting;
                Detail = "Sending the request to the background service...";
                await _pipe.ConnectTunnelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            State = TunnelState.Faulted;
            Error = ex.Message;
        }
    }

    // ------------------------------------------------------- updates from the service

    private void OnStatus(StatusMessage status) => Dispatcher.UIThread.Post(() =>
    {
        State = status.State;
        Detail = status.Detail;
        Error = status.Error;
        PingMs = status.TunnelPingMs;
        LossRatio = status.LossRatio;
        GameRunning = status.GameRunning;
        GameName = status.GameName;
        RelayName = status.RelayName;
        ActiveRoutes = status.ActiveRoutes;
        PacketsSent = status.PacketsSent;
        PacketsReceived = status.PacketsReceived;
    });

    private void OnDisconnected(string reason) => Dispatcher.UIThread.Post(() =>
    {
        State = TunnelState.Disconnected;
        Detail = reason;
        PingMs = null;
        LossRatio = null;
        ActiveRoutes = 0;
        PacketsSent = 0;
        PacketsReceived = 0;
    });

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
