using System.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Network;

/// <summary>
/// Whether this machine sends by the strong host model, measured at every connect. With it - every Windows default -
/// a TCP connection keeps the interface that owns its source address whatever route goes in later, which is the only
/// reason a TCP game's connections opened on the line before Connect need no hold (Core TcpHold, `TunnelCheck service
/// tcp` part 4). With forwarding or weak host send on the virtual adapter or the line (IPEnableRouter, connection
/// sharing, some VPN clients) a route going in can take such a connection into the tunnel, where it ends.
///
/// Logged, and sent with every quality record as "hostModel", so the data says whether any player needs the hold that
/// was taken out again (commit eef21aa has it). Read from the interfaces' own rows - no names, no addresses.
/// </summary>
internal static class HostModel
{
    /// <summary>What the last connect saw: "strong", or each weak side, as "adapter=forwarding+weakSend;line=forwarding". Null before one.</summary>
    public static volatile string? Current;

    /// <summary>Reads the virtual adapter's row and the line's - the interface the relay is reached through - and logs them.</summary>
    public static void Measure(uint adapterIndex, IPAddress relay, Action<string> log)
    {
        try
        {
            var sides = new List<(string Name, IpHelper.InterfaceRow? Row)> { ("adapter", IpHelper.ReadInterface(adapterIndex)) };
            if (RouteManager.GetRouteTo(relay) is { } line) sides.Add(("line", IpHelper.ReadInterface(line.InterfaceIndex)));

            var weak = new List<string>();
            foreach (var (name, row) in sides)
            {
                if (row is not { } r) continue;
                var what = new List<string>();
                if (r.ForwardingEnabled != 0) what.Add("forwarding");
                if (r.WeakHostSend != 0) what.Add("weakSend");
                if (what.Count > 0) weak.Add($"{name}={string.Join('+', what)}");
            }
            Current = weak.Count == 0 ? "strong" : string.Join(';', weak);
            log(weak.Count == 0
                ? $"Host model: strong host on {string.Join(" and ", sides.Where(s => s.Row is not null).Select(s => s.Name))} - " +
                  "a route going in leaves the connections opened before it where they are."
                : $"Host model: WEAK ({Current}) - a route going in can take a TCP connection opened before it into the " +
                  "tunnel, where it ends; a TCP game started before Connect may lose its connection once.");
        }
        catch (Exception ex)
        {
            log($"Host model: could not be read ({ex.Message}).");
        }
    }
}
