using System.Diagnostics;
using System.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// The TCP half of discovery, for a game that plays over TCP (<see cref="Core.Profiles.GameEntry.TcpGameplay"/>):
/// the game's own established connections and how many data segments it has sent on each, merged by remote address
/// into the same <see cref="FlowRow"/>s ETW gives for UDP, so <see cref="GameDestinationRecorder"/> judges both by one
/// rule - a rate to one address outside the profile.
///
/// Left out before any counting:
///   - ports 80 and 443. Web and CDN traffic - the game's store, patch notes, analytics - never plays a match, and a
///     shared CDN edge must never become a game range (Aniimo, 2026-10-13: 443 to Tencent, Alibaba US, AWS, Cloudflare).
///   - connections opened from the tunnel's inner address: those ride the tunnel, which counts them already.
///   - the game's lobby addresses: routed as /32s on purpose, never a range.
///
/// Counts are cumulative per address and only go up: a connection that closes keeps what it carried, and a new one to
/// the same server adds to it, so a match that reconnects is not reset to zero.
///
/// Runs on the recorder's poll thread only; not thread-safe.
/// </summary>
internal sealed class TcpFlowSampler(IReadOnlySet<IPAddress> lobby, Func<IPAddress?> tunnelAddress, Action<string> log)
{
    private static readonly HashSet<int> WebPorts = [80, 443];

    private readonly record struct Conn(IPEndPoint Local, IPEndPoint Remote);

    private sealed class Seen
    {
        public ulong? LastOut;
        public bool Enabled;
    }

    private readonly Dictionary<Conn, Seen> _conns = [];
    private readonly Dictionary<IPAddress, FlowRow> _rows = [];
    private bool _enableFailureLogged;

    // Windows, unless TunnelCheck stands in for it.
    internal Func<IReadOnlySet<uint>, List<TcpConnections.Owned>> Connections { get; init; } = TcpConnections.OfProcesses;
    internal Func<IPEndPoint, IPEndPoint, uint> EnableCounters { get; init; } = TcpEStats.Enable;
    internal Func<IPEndPoint, IPEndPoint, TcpEStats.Counters?> ReadCounters { get; init; } = TcpEStats.Read;

    /// <summary>Addresses counted over the whole game session - for the closing log line.</summary>
    public int Addresses { get; private set; }

    /// <summary>Reads the game's connections now and returns every address's running total.</summary>
    public List<FlowRow> Sample(IReadOnlySet<uint> pids)
    {
        var now = Stopwatch.GetTimestamp();
        var inner = tunnelAddress();
        var live = new HashSet<Conn>();

        foreach (var owned in Connections(pids))
        {
            if (owned.State != 5) continue;   // Established only
            var remote = owned.Remote;
            if (WebPorts.Contains(remote.Port) || Destinations.IsLocal(remote.Address) || lobby.Contains(remote.Address)) continue;
            if (inner is not null && owned.Local.Equals(inner)) continue;

            var conn = new Conn(new IPEndPoint(owned.Local, owned.LocalPort), remote);
            live.Add(conn);
            if (!_conns.TryGetValue(conn, out var seen))
            {
                seen = new Seen();
                _conns[conn] = seen;
                var error = EnableCounters(conn.Local, conn.Remote);
                seen.Enabled = error == 0;
                if (!seen.Enabled && !_enableFailureLogged)
                {
                    _enableFailureLogged = true;
                    log($"Discovery: Windows would not count a TCP connection's segments (error {error}) - " +
                        "TCP servers are not judged on this machine.");
                }
            }
            if (!seen.Enabled || ReadCounters(conn.Local, conn.Remote) is not { } counters) continue;

            // The first read is a baseline, never a delta: collection may have been on since an earlier listen (the
            // counters do not restart when it is switched on again), and a gateway's half hour must not land in one window.
            var delta = seen.LastOut is { } last && counters.DataSegsOut >= last ? counters.DataSegsOut - last : 0;
            seen.LastOut = counters.DataSegsOut;

            if (!_rows.TryGetValue(remote.Address, out var row))
            {
                row = new FlowRow { Address = remote.Address, FirstTimestamp = now };
                _rows[remote.Address] = row;
                Addresses++;
            }
            row.Sent += (long)delta;
            row.LastTimestamp = now;
            row.Ports.Add(remote.Port);
        }

        foreach (var gone in _conns.Keys.Where(c => !live.Contains(c)).ToList()) _conns.Remove(gone);
        return [.. _rows.Values];
    }

    /// <summary>Forgets every count - listening stopped, and the next listen starts a fresh window.</summary>
    public void Clear()
    {
        _conns.Clear();
        _rows.Clear();
    }
}
