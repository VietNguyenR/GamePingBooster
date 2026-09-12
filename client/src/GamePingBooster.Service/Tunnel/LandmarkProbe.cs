using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Which datacentre the game is going to put this player in, measured rather than assumed.
///
/// Online games decide that for themselves: before a match PUBG probes one endpoint per Azure
/// region on UDP 8081, Valve SDR checks regional coordinator clusters, and they pick the nearest.
/// Those endpoints are stable across sessions - unlike gameplay servers, which are allocated per match
/// and never repeat - so they can be written into the profile as a <see cref="RegionEntry.Landmarks"/> list
/// and used as a stand-in for the region itself. They sit in the region they represent and they answer
/// ICMP, which is all this needs.
///
/// Two things are measured with them, and the difference matters:
///
///   - here, over the PHYSICAL path, to work out which region the game will choose. That has to
///     be the honest ISP path, because that is what the game measures too (see the decision in
///     HANDOFF section 6a). Get this wrong and we compare relays against the wrong destination.
///   - in <see cref="TunnelClient.MeasureThroughTunnelAsync"/>, through each candidate tunnel,
///     to work out what the player's ping will actually be on that relay.
///
/// A region with no landmarks cannot be ranked and is left out. That is not a failure: it means
/// the profile has not recorded a probe endpoint for it yet, and the caller falls back to
/// comparing relays on the first leg alone, which is what this client did before any of this
/// existed.
/// </summary>
internal static class LandmarkProbe
{
    /// <summary>How long a landmark may take to answer before it is treated as unreachable.</summary>
    private const int TimeoutMs = 800;

    /// <summary>
    /// Probes taken per landmark. Best of two is kept to balance accuracy and speed.
    /// </summary>
    private const int Attempts = 2;

    internal sealed record Result(string RegionId, string RegionName, IPAddress Landmark, double RttMs);

    /// <summary>
    /// Measures every region that declares a landmark, over the physical path in parallel, best first.
    /// Regions that declare none, or whose landmarks all stay silent, are not in the list.
    /// </summary>
    public static async Task<IReadOnlyList<Result>> RankRegionsAsync(
        IEnumerable<RegionEntry> regions, Action<string> log, CancellationToken ct)
    {
        var list = regions.ToList();
        if (list.Count == 0) return [];

        var results = new ConcurrentBag<Result>();

        await Parallel.ForEachAsync(list, new ParallelOptions
        {
            MaxDegreeOfParallelism = 16,
            CancellationToken = ct
        }, async (region, token) =>
        {
            Result? best = null;
            foreach (var text in region.Landmarks)
            {
                if (!IPAddress.TryParse(text, out var address)) continue;

                var rtt = await MeasureAsync(address, token).ConfigureAwait(false);
                if (rtt is null) continue;
                if (best is null || rtt < best.RttMs)
                {
                    best = new Result(region.Id, region.Name, address, rtt.Value);
                }
            }

            if (best is not null)
            {
                results.Add(best);
            }
        }).ConfigureAwait(false);

        var sorted = results.ToList();
        sorted.Sort((a, b) => a.RttMs.CompareTo(b.RttMs));

        foreach (var r in sorted)
        {
            log($"Landmark {r.RegionName} ({r.RegionId}) probed at {r.RttMs:F0} ms");
        }

        return sorted;
    }

    /// <summary>Best of <see cref="Attempts"/> echoes over the physical path, or null if silent.</summary>
    private static async Task<double?> MeasureAsync(IPAddress address, CancellationToken ct)
    {
        // 32 bytes of payload, matching what `ping` sends on Windows: a runt packet is not
        // necessarily treated the way a game packet is.
        var payload = new byte[32];
        double? best = null;

        using var ping = new Ping();
        for (var i = 0; i < Attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(address, TimeoutMs, payload).ConfigureAwait(false);
                if (reply.Status != IPStatus.Success) continue;

                // RoundtripTime is whole milliseconds, which is coarse but honest; the numbers
                // this decides between are tens of milliseconds apart.
                double rtt = reply.RoundtripTime;
                if (best is null || rtt < best) best = rtt;
            }
            catch (PingException)
            {
                // No route, no name resolution, no raw socket - all mean the same thing here.
                return best;
            }
        }
        return best;
    }
}
