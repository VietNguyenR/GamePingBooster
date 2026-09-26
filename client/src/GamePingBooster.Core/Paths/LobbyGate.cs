namespace GamePingBooster.Core.Paths;

/// <summary>
/// When the region planner may measure: the game is in its lobby, not in a match (docs/MULTI-TUNNEL.md 5.5). Asked
/// once per supervisor pass (every 5 s) with the game's UDP count over every tunnel - a count that is never cleared.
///
/// Two ways to be sure of the lobby:
///
///   - <b>Nothing yet.</b> The game has sent no UDP into any tunnel since its routes went in (<see cref="Arm"/>).
///     A match sends 9-150 packets a second, so a count still at zero is no match - the first pass is enough. This
///     is the usual case: connect with the game open in its lobby, or open it after connecting.
///   - <b>Quiet for two passes.</b> Otherwise - a lobby trickle like Naraka's one packet a second, a match just
///     ended, a connect in the middle of one - the rate stays under <see cref="PacketsPerSecond"/> over two passes
///     in a row, the second or later window each at least <see cref="MinWindowMs"/> long.
///
/// It was always the second rule, with its first window starting at the first supervisor pass: fifteen seconds
/// from connect to a plan, and a match starting inside them went home for its whole length. Neither rule can start
/// a pass in a match: the pass stops itself when match traffic appears.
///
/// Supervisor thread for <see cref="Observe"/>; <see cref="Arm"/> comes from the game watcher, hence the lock. Time
/// is passed in, in milliseconds, so a test can drive it.
/// </summary>
public sealed class LobbyGate
{
    /// <summary>Under this, into the tunnels, is the lobby. Not zero: Naraka trickles one a second from its lobby.</summary>
    public const double PacketsPerSecond = 3;

    /// <summary>A window shorter than this says nothing: one packet in 200 ms reads as five a second.</summary>
    public const long MinWindowMs = 2_000;

    private readonly object _gate = new();
    private object? _tunnel;
    private long _atMs;
    private long _packets;
    private bool _armed;
    private int _quietPasses;

    /// <summary>Forget everything: a new connection, or the planner just ran.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _tunnel = null;
            _armed = false;
            _quietPasses = 0;
        }
    }

    /// <summary>
    /// The game's routes just went in on <paramref name="tunnel"/>, whose tunnels had carried
    /// <paramref name="packets"/> game UDP packets so far. The count starts here.
    /// </summary>
    public void Arm(object tunnel, long nowMs, long packets)
    {
        lock (_gate)
        {
            _tunnel = tunnel;
            _atMs = nowMs;
            _packets = packets;
            _armed = true;
            _quietPasses = 0;
        }
    }

    /// <summary>One supervisor pass. True when the planner may measure now.</summary>
    public bool Observe(object tunnel, long nowMs, long packets)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(tunnel, _tunnel) || packets < _packets)
            {
                // Another tunnel, or a count that went back (a tunnel closed): start again from here.
                _tunnel = tunnel;
                _atMs = nowMs;
                _packets = packets;
                _armed = false;
                _quietPasses = 0;
                return false;
            }

            if (_armed && packets == _packets) return true;

            var window = nowMs - _atMs;
            if (window < MinWindowMs) return false;

            var rate = (packets - _packets) * 1000.0 / window;
            _quietPasses = rate < PacketsPerSecond ? _quietPasses + 1 : 0;
            _atMs = nowMs;
            _packets = packets;
            _armed = false;
            return _quietPasses >= 2;
        }
    }
}
