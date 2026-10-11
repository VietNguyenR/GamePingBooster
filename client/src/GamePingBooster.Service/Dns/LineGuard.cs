using System.Collections.Concurrent;
using System.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// The tunnel's routes, kept off addresses the line is using for another name.
///
/// CDN edges are shared. VNPT, 2026-10-07 02:35: prod-live-cfentry was answered for the line with 13.227.185.100, .109
/// and .127 (CloudFront HAN, in-country); 0.7 s later accounts.pubg.com's tunnel lookup routed 13.227.185.127/32 among
/// its own edges, and the game's 12 MB from cfentry rode the relay at its cap - the lobby lagged. LocalResolver's
/// KeepOffTheTunnel covers the other order (route first, line answer after); this covers a route asked for after the
/// line was handed the address. An address is held off the tunnel while:
///   - it was answered for the line to ANOTHER name within <see cref="LineAnswerHeld"/> - longer than the answer's TTL,
///     because a browser (the lobby is Coherent, a Chromium) reuses an address past it; or
///   - a TCP connection to it is open and not already through the tunnel - a route added now would move that flow
///     into the relay mid-way, where it dies. This catches flows older than the window and unclaimed names.
/// Only some of a name's addresses are ever held: when all of them would be, all are routed as before - a listed name
/// must still work on a line that cuts it (FPT) - except the ones another program has an ESTABLISHED connection to.
/// Such a flow proves the line serves that address, and the route would kill it. FPT, 2026-10-11: Steam's live CM
/// connections (cmp1-tyo3, cmp2-sea1, cmp1-lax1, ext1-maa2 - cut by name for TLS, but Steam was connected to them
/// over the line) and its 118.69.16.x connections for api.steampowered.com were all routed 3 s before PUBG started,
/// and PUBG said "failed to authenticate with the external provider" - the error Steam being offline gives.
/// A route is taken back only through <see cref="Withdraw"/>, and never from an address with a live connection.
/// </summary>
internal sealed class LineGuard : IUnblockRoutes
{
    /// <summary>How long an address answered for the line stays off the tunnel for other names.</summary>
    internal static readonly TimeSpan LineAnswerHeld = TimeSpan.FromMinutes(10);

    private const int MaxRemembered = 2000;
    private const int MaxSaid = 300;

    private readonly IUnblockRoutes _inner;
    private readonly Action<string> _log;
    private readonly Func<IReadOnlySet<IPAddress>> _openRemotes;
    private readonly Func<IReadOnlySet<IPAddress>> _liveRemotes;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Address -> the name it was answered for on the line, and until when it is held.</summary>
    private readonly ConcurrentDictionary<IPAddress, (string Name, DateTimeOffset Until)> _line = new();

    private readonly ConcurrentDictionary<string, byte> _said = new();

    public LineGuard(IUnblockRoutes inner, Action<string> log, Func<IReadOnlySet<IPAddress>>? openRemotes = null,
        Func<DateTimeOffset>? now = null, Func<IReadOnlySet<IPAddress>>? liveRemotes = null)
    {
        _inner = inner;
        _log = log;
        _openRemotes = openRemotes ?? TcpConnections.OpenRemotes;
        _liveRemotes = liveRemotes ?? TcpConnections.LiveRemotesOfOthers;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Ready => _inner.Ready;

    public bool Tunnelled(IPAddress address) => _inner.Tunnelled(address);

    /// <summary>The line was handed <paramref name="addresses"/> for <paramref name="name"/>.</summary>
    public void AnsweredForLine(string name, IReadOnlyList<IPAddress> addresses)
    {
        var now = _now();
        if (_line.Count > MaxRemembered)
        {
            foreach (var (address, held) in _line)
            {
                if (held.Until <= now) _line.TryRemove(address, out _);
            }
            if (_line.Count > MaxRemembered) return;
        }

        var until = now.Add(LineAnswerHeld);
        foreach (var address in addresses) _line[address] = (name, until);
    }

    public bool Route(string name, IReadOnlyList<IPAddress> addresses)
    {
        if (!_inner.Ready || addresses.Count == 0) return _inner.Route(name, addresses);

        var now = _now();
        IReadOnlySet<IPAddress>? open = null;
        var held = new List<(IPAddress Address, string Why)>();
        foreach (var address in addresses)
        {
            if (_line.TryGetValue(address, out var line) && line.Until > now &&
                !string.Equals(line.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                held.Add((address, $"answered for the line to {line.Name}"));
                continue;
            }

            open ??= _openRemotes();
            if (open.Contains(address) && !_inner.Tunnelled(address))
            {
                held.Add((address, "a connection to it is open over the line"));
            }
        }

        if (held.Count == 0) return _inner.Route(name, addresses);

        if (held.Count == addresses.Count)
        {
            // Routed as before, all but the addresses another program is using over the line right now.
            var live = _liveRemotes();
            List<IPAddress> busy = [.. addresses.Where(a => live.Contains(a) && !_inner.Tunnelled(a))];
            if (busy.Count == addresses.Count)
            {
                Say(name, $"Unblock: every address for {name} carries another program's live connection over the line - " +
                          $"not routed through the tunnel, which would cut it; the line evidently serves them.");
                return false;
            }
            if (busy.Count > 0)
            {
                Say(name, $"Unblock: every address for {name} is in use on the line - routed through the tunnel as before, " +
                          $"except {string.Join(", ", busy)}, which another program has a live connection to over the line.");
                return _inner.Route(name, [.. addresses.Except(busy)]);
            }
            Say(name, $"Unblock: every address for {name} is in use on the line ({held[0].Why}) - routed through the tunnel as before.");
            return _inner.Route(name, addresses);
        }

        foreach (var (address, why) in held)
        {
            Say($"{name} {address}", $"Unblock: {address} for {name} is kept off the tunnel - {why} (a shared edge).");
        }
        var keep = held.Select(h => h.Address).ToHashSet();
        return _inner.Route(name, [.. addresses.Where(a => !keep.Contains(a))]);
    }

    /// <summary>
    /// Passed on without any address another program has an established connection to: that flow would die with the
    /// route. One still opening is not spared - through a tunnel that cannot reach the address it is waiting for nothing,
    /// and is exactly what the withdrawal is for.
    /// </summary>
    public void Withdraw(string name, IReadOnlyList<IPAddress> addresses, string why)
    {
        if (!_inner.Ready || addresses.Count == 0) return;
        var live = _liveRemotes();
        List<IPAddress> free = [.. addresses.Where(a => !live.Contains(a))];
        if (free.Count > 0) _inner.Withdraw(name, free, why);
    }

    private void Say(string key, string line)
    {
        if (_said.Count < MaxSaid && _said.TryAdd(key, 0)) _log(line);
    }
}
