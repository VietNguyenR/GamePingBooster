using static GamePingBooster.Service.Discovery.EtwNative;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// The one ETW session discovery reads, held from Connect to Disconnect and paused whenever nothing listens.
///
/// Why held and not opened per listen: EA's Javelin anti-cheat (Battlefield 6, 2026-10-14) refuses every NEW ETW
/// session while the game runs - logman as Administrator was refused too - and refuses to STOP one, but a session
/// opened before the game started kept running and saw the whole match (27k events to its server). The recorder
/// used to open a session for every listen and close it after, so on that game it listened once at most. Its
/// driver can also load before the process watcher sees the game, so opening at game start is already too late:
/// the session is opened at Connect.
///
/// Held for every game, not only the ones known to block, so no list of anti-cheats has to be kept. Paused, an
/// event is dropped on arrival (<see cref="KernelNetworkTrace.Paused"/>); the session only asks for the four UDP
/// event ids, so even a busy machine costs a fraction of a percent of one core.
///
/// A stop the anti-cheat refuses is retried every <see cref="RetryStop"/> until it succeeds - it does the moment the
/// game exits - and a Connect in between takes the still-running session back instead of failing to open a new one.
///
/// Every method takes the same lock; the recorder's chain and its poll timer both call in.
/// </summary>
internal sealed class HeldTrace(string sessionName, Action<string> log) : IDisposable
{
    private static readonly TimeSpan RetryStop = TimeSpan.FromSeconds(30);

    // Settled once per service process: the answer depends on Windows, not on the session.
    private static DecodingRules? _rules;

    private readonly object _gate = new();
    private readonly SinkSwitch _sink = new();
    private KernelNetworkTrace? _trace;
    private Timer? _retry;
    private bool _closing;
    private bool _disposed;

    /// <summary>Why the last open failed, or null. A message for the log, already in words.</summary>
    public string? Failure { get; private set; }

    /// <summary>True when that failure was Windows refusing a new session (access denied) - an anti-cheat, in a
    /// LocalSystem service that has every right to open one.</summary>
    public bool FailureWasRefused { get; private set; }

    public DecodingRules? Rules => _rules;

    public bool EventIdFilterApplied
    {
        get { lock (_gate) return _trace?.EventIdFilterApplied ?? false; }
    }

    /// <summary>
    /// Opens the session, or keeps the one already running - including one whose stop the anti-cheat refused.
    /// True when a session is open; false with <see cref="Failure"/> set otherwise. Never throws.
    /// </summary>
    public bool Open(string why)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_trace is not null)
            {
                if (_closing)
                {
                    _closing = false;
                    _retry?.Dispose();
                    _retry = null;
                    log("Discovery: kept the ETW session Windows would not let stop - it is this connection's now.");
                }
                return true;
            }

            var table = new FlowTable((uint)Environment.ProcessId);
            KernelNetworkTrace trace;
            try
            {
                _sink.Target = table;
                trace = KernelNetworkTrace.Start(sessionName, _sink, log, ThreadPriority.BelowNormal);
            }
            catch (Exception ex)
            {
                _sink.Target = null;
                FailureWasRefused = ex is EtwException { Error: ErrorAccessDenied };
                Failure = FailureWasRefused
                    ? "Windows refused a new ETW session. A running game's anti-cheat does that (EA's does, for " +
                      "Battlefield 6) - Connect before starting the game and discovery keeps the session it opened"
                    : ex.Message;
                return false;
            }

            try
            {
                _rules ??= SelfTest.Run(table, log);
            }
            catch (Exception ex)
            {
                // Whatever the self-test threw, the session it was testing must not be left running.
                _sink.Target = null;
                trace.Dispose();
                FailureWasRefused = false;
                Failure = $"the ETW self-test failed: {ex.Message}";
                return false;
            }

            trace.Paused = true;
            _sink.Target = null;
            _trace = trace;
            Failure = null;
            FailureWasRefused = false;
            log($"Discovery: ETW session open ({why}), paused until a game needs it.");
            return true;
        }
    }

    /// <summary>Starts delivering events into <paramref name="table"/>. False when no session is open.</summary>
    public bool Attach(FlowTable table)
    {
        lock (_gate)
        {
            if (_trace is not { } trace || _closing) return false;
            table.Rules = _rules;
            _sink.Target = table;
            trace.Paused = false;
            return true;
        }
    }

    /// <summary>Stops delivering; the session keeps running, paused.</summary>
    public void Detach()
    {
        lock (_gate)
        {
            if (_trace is { } trace) trace.Paused = true;
            _sink.Target = null;
        }
    }

    /// <summary>Every event the session delivered and read - decoded UDP, other ids, undecodable - for the cost
    /// guard. Paused events are left out: they cost nothing worth guarding.</summary>
    public long ReadEvents
    {
        get { lock (_gate) return _trace is { } t ? t.UdpEvents + t.OtherEvents + t.UndecodedEvents : 0; }
    }

    public long UdpEvents
    {
        get { lock (_gate) return _trace?.UdpEvents ?? 0; }
    }

    public long EventsLost
    {
        get { lock (_gate) return _trace?.QueryLosses().EventsLost ?? 0; }
    }

    /// <summary>Disconnect: stops the session, or - when the anti-cheat refuses - keeps trying until it can.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_trace is null || _closing) return;
            if (_trace is { } trace) trace.Paused = true;
            _sink.Target = null;
            if (TryStopLocked()) return;

            _closing = true;
            log($"Discovery: Windows would not stop the ETW session (a running game's anti-cheat refuses it) - " +
                $"paused, trying again every {RetryStop.TotalSeconds:F0} s until the game exits.");
            _retry = new Timer(_ => RetryClose(), null, RetryStop, RetryStop);
        }
    }

    private void RetryClose()
    {
        lock (_gate)
        {
            if (!_closing || !TryStopLocked()) return;
            _closing = false;
            _retry?.Dispose();
            _retry = null;
            log("Discovery: the ETW session is stopped now.");
        }
    }

    private bool TryStopLocked()
    {
        if (_trace is not { } trace) return true;
        if (!trace.TryStop()) return false;
        _trace = null;
        return true;
    }

    /// <summary>The service is stopping: one more try, then the reader goes regardless, and the log says how to stop
    /// what is left. The next start stops a session left under this name.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _retry?.Dispose();
            _retry = null;
            _sink.Target = null;
            _trace?.Dispose();
            _trace = null;
        }
    }

    /// <summary>The sink the session was started with, forwarding to whichever table is listening now.</summary>
    private sealed class SinkSwitch : IUdpEventSink
    {
        private volatile FlowTable? _target;

        public FlowTable? Target
        {
            get => _target;
            set => _target = value;
        }

        public void OnUdpEvent(in RawUdpEvent e) => _target?.OnUdpEvent(in e);
    }
}
