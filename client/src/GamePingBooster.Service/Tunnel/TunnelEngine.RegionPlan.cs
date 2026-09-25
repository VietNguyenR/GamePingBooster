using System.Diagnostics;
using System.Net;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// The region planner's measuring pass - docs/MULTI-TUNNEL.md 5.5, phase C.
///
/// Once per connection and game, in the lobby, every region of the game with a landmark is measured three
/// ways: through the home tunnel, over the player's own line, and through every other relay that carries the
/// game (each way into it, the fastest kept). <see cref="RegionPlanner"/> turns the numbers into a path per
/// region, and the plan goes into the log and the connection-quality record.
///
/// In this version NOTHING IS MOVED, in either mode: the data plane has one tunnel. "on" is recorded as what it
/// would have done, and says so. The pass exists to find out, from real players on real lines, how often a
/// region would leave home and by how much - before a second tunnel carries a single packet.
///
/// The measuring follows the rules every relay measurement here follows, each one learned the hard way:
///
///   - the relay in use is measured through its live tunnel, never by a handshake (G5 - a second handshake
///     moves the live session);
///   - every other relay is handshaken once per way in, one way at a time, the previous way closed without a
///     Disconnect so the next resumes the same session, and the last closed WITH one;
///   - the same instrument on every path: the median of <see cref="RescanScore.Samples"/> echoes, at least
///     <see cref="RescanScore.MinAnswered"/> answered, or no number at all;
///   - it runs on the supervisor, so it can never overlap a rescan between matches, a reconnect or a move;
///   - it stops the moment a match starts loading, or the home tunnel goes quiet, and is tried again later.
///
/// It costs the player nothing they can feel: a few hundred small echoes in the lobby, and a session on each
/// other relay for a few seconds.
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>The whole pass. Past it the plan is made from what was measured and marked incomplete.</summary>
    private static readonly TimeSpan RegionPlanBudget = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Game UDP into the tunnel below this rate, over two supervisor passes (ten seconds), is the lobby. Not
    /// zero: Naraka trickles one packet a second to a server from the lobby, and a match runs 9-150.
    /// </summary>
    private const double LobbyPacketsPerSecond = 3;

    /// <summary>A pass interrupted by a match or a quiet tunnel is tried again, this many times per connection and game.</summary>
    private const int RegionPlanTries = 3;

    /// <summary>The home tunnel quiet this long mid-pass: stop and let the supervisor look at it.</summary>
    private static readonly TimeSpan RegionPlanHomeQuiet = TimeSpan.FromSeconds(5);

    /// <summary>Tries per (game, home relay); -1 once a pass completed. Supervisor only, reset per connect.</summary>
    private readonly Dictionary<string, int> _regionPlanTries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's UDP count at the last supervisor pass, for the lobby rate. Supervisor only.</summary>
    private (TunnelClient Tunnel, long AtMs, long Packets)? _lobbyLast;

    /// <summary>Consecutive supervisor passes under <see cref="LobbyPacketsPerSecond"/>. Supervisor only.</summary>
    private int _lobbyQuietPasses;

    private void ResetRegionPlanning()
    {
        _regionPlanTries.Clear();
        _lobbyLast = null;
        _lobbyQuietPasses = 0;
    }

    /// <summary>One supervisor pass: runs the planner's measuring pass when it is due - see the class summary.</summary>
    private async Task PlanRegionsIfDueAsync(TunnelClient tunnel, CancellationToken ct)
    {
        var game = _game;
        var home = _relay;
        var profile = _profile;
        if (game is null || home is null || profile is null || !ReferenceEquals(tunnel, _tunnel)) return;
        if (!(_watcher?.IsGameRunning ?? false))
        {
            _lobbyLast = null;
            _lobbyQuietPasses = 0;
            return;
        }

        var (mode, source) = RegionRouting.Resolve(_config.RegionRouting, game.RegionRouting, game.LandmarksRouted);
        if (mode == RegionRoutingMode.Off) return;

        var key = $"{game.Id}|{RelayPaths.RelayIdOf(home)}";
        var tries = _regionPlanTries.GetValueOrDefault(key);
        if (tries < 0 || tries >= RegionPlanTries) return;

        // The lobby: the game's UDP into the tunnel under the rate, over two passes in a row.
        var now = NowMs();
        var packets = tunnel.Destinations.UdpPackets;
        var last = _lobbyLast;
        _lobbyLast = (tunnel, now, packets);
        if (last is not { } previous || !ReferenceEquals(previous.Tunnel, tunnel) || now <= previous.AtMs)
        {
            _lobbyQuietPasses = 0;
            return;
        }
        var rate = (packets - previous.Packets) * 1000.0 / (now - previous.AtMs);
        _lobbyQuietPasses = rate < LobbyPacketsPerSecond ? _lobbyQuietPasses + 1 : 0;
        if (_lobbyQuietPasses < 2) return;

        // One region is what connect already measured; a plan needs something to choose between.
        if (game.Regions.Count < 2 || !game.Regions.Any(r => FirstLandmark(r) is not null))
        {
            _regionPlanTries[key] = -1;
            _log($"Region plan ({mode}, from {source}): {game.Name} has " +
                 (game.Regions.Count < 2 ? "one region" : "no region with a landmark") + " - nothing to plan.");
            return;
        }

        _regionPlanTries[key] = tries + 1;
        if (await PlanRegionsAsync(tunnel, game, home, profile, mode, source, ct).ConfigureAwait(false))
        {
            _regionPlanTries[key] = -1;
        }
        _lobbyLast = null;
        _lobbyQuietPasses = 0;
    }

    /// <summary>
    /// Measures, plans, logs and records. Returns false when the pass was interrupted in a way worth trying
    /// again - a match starting, the home tunnel going quiet - and true otherwise, including a pass the budget
    /// cut short, which is recorded as incomplete: a second try would only be cut short again.
    /// </summary>
    private async Task<bool> PlanRegionsAsync(TunnelClient tunnel, GameEntry game, RelayEntry home, ProfileBundle profile,
        RegionRoutingMode mode, string source, CancellationToken ct)
    {
        var homeRelayId = RelayPaths.RelayIdOf(home);
        var started = Stopwatch.GetTimestamp();
        var packetsAtStart = tunnel.Destinations.UdpPackets;
        var regions = game.Regions.Select(r => (Region: r, Landmark: FirstLandmark(r))).ToList();
        var measurable = regions.Where(r => r.Landmark is not null).Select(r => (r.Region, Landmark: r.Landmark!)).ToList();

        var homeMs = new Dictionary<string, double?>(StringComparer.Ordinal);
        var directMs = new Dictionary<string, double?>(StringComparer.Ordinal);
        var viaMs = measurable.ToDictionary(r => r.Region.Id, _ => new Dictionary<string, double>(StringComparer.Ordinal), StringComparer.Ordinal);
        var viaWay = measurable.ToDictionary(r => r.Region.Id, _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var relaysMeasured = new List<string>();
        string? stopped = null;
        var retry = false;

        _log($"Region plan ({mode}, from {source}): measuring {measurable.Count} of {regions.Count} regions of {game.Name} " +
             $"through {home.Name} [{home.Id}], over your own line and through the other relays - the median of " +
             $"{RescanScore.Samples} echoes each." +
             (mode == RegionRoutingMode.On ? " This version records the plan and moves nothing." : ""));

        // Why the pass has to stop now, or null. A match loading and a quiet home tunnel are worth another try.
        string? Interrupted()
        {
            if (!ReferenceEquals(tunnel, _tunnel) || _state != TunnelState.Connected) return "the tunnel changed";
            if (tunnel.SinceLastHeard >= RegionPlanHomeQuiet)
            {
                retry = true;
                return $"{home.Name} has been quiet {tunnel.SinceLastHeard.TotalSeconds:F0} s";
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (tunnel.Destinations.UdpPackets - packetsAtStart > 10 + LobbyPacketsPerSecond * elapsed)
            {
                retry = true;
                return "the game started sending to a server - a match is loading";
            }
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RegionPlanBudget);
        var token = budget.Token;
        try
        {
            // Through home: the live tunnel, never a handshake (G5).
            foreach (var (region, landmark) in measurable)
            {
                if ((stopped = Interrupted()) is not null) break;
                homeMs[region.Id] = RescanScore.Median(await SampleLiveAsync(tunnel, landmark, token).ConfigureAwait(false));
            }

            // Over the player's own line. A landmark inside a routed range would be measured through the tunnel
            // and called direct; the profile rules keep that from happening, and this keeps it from counting.
            foreach (var (region, landmark) in measurable)
            {
                if (stopped is not null || (stopped = Interrupted()) is not null) break;
                if (IsRoutedIntoTunnel(landmark))
                {
                    directMs[region.Id] = null;
                    continue;
                }
                directMs[region.Id] = RescanScore.Median(await LandmarkProbe.SampleAsync(
                    landmark, RescanScore.Samples, LiveSampleSpacing, ProbeTimeoutMs, token).ConfigureAwait(false));
            }

            _log("  " + string.Join(", ", measurable.Select(r =>
                $"{r.Region.Id}: home {Ms(homeMs.GetValueOrDefault(r.Region.Id))}, direct {Ms(directMs.GetValueOrDefault(r.Region.Id))}")));

            // Every other relay that carries the game, one way in at a time.
            var others = RelaysForGame(game)
                .Where(r => r.ViaRelayId is null && !r.Id.Equals(homeRelayId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);
            foreach (var relay in others)
            {
                if (stopped is not null) break;
                TunnelClient? open = null;
                try
                {
                    foreach (var way in RelayPaths.DoorsOf(profile.Relays, relay.Id))
                    {
                        if ((stopped = Interrupted()) is not null) break;
                        if (IsRoutedIntoTunnel(way))
                        {
                            _log($"  {way.Name} [{way.Id}]: skipped - its address is inside a routed game range.");
                            continue;
                        }

                        // The next way into this relay resumes the same session, so the last one goes quietly.
                        Abandon(open);
                        open = null;
                        open = await HandshakeForPlanAsync(way, psk, token).ConfigureAwait(false);
                        if (open is null) continue;

                        var line = new List<string>();
                        foreach (var (region, landmark) in measurable)
                        {
                            if ((stopped = Interrupted()) is not null) break;
                            var samples = new List<double?>(RescanScore.Samples);
                            for (var i = 0; i < RescanScore.Samples; i++)
                            {
                                samples.Add(await open.MeasureThroughTunnelAsync(landmark, attempts: 1, token).ConfigureAwait(false));
                            }
                            var median = RescanScore.Median(samples);
                            line.Add($"{region.Id} {Ms(median)}");
                            if (median is not { } ms) continue;
                            if (!viaMs[region.Id].TryGetValue(relay.Id, out var kept) || ms < kept)
                            {
                                viaMs[region.Id][relay.Id] = ms;
                                viaWay[region.Id][relay.Id] = way.Id;
                            }
                        }
                        _log($"  {way.Name} [{way.Id}]: {string.Join(", ", line)}");
                        if (!relaysMeasured.Contains(relay.Id, StringComparer.OrdinalIgnoreCase)) relaysMeasured.Add(relay.Id);
                        if (stopped is not null) break;
                    }
                }
                finally
                {
                    // With a Disconnect: the relay's session goes back to its pool now rather than in 90 s.
                    open?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopped = $"the {RegionPlanBudget.TotalSeconds:F0} s budget ran out";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Region plan: measuring failed ({ex.Message}) - no plan this time.");
            return true;
        }

        if (retry)
        {
            _log($"Region plan: stopped - {stopped}. It is tried again in the next quiet spell.");
            return false;
        }

        var measurements = regions.Select(r => new RegionMeasurement(
            r.Region.Id,
            r.Landmark is not null,
            homeMs.GetValueOrDefault(r.Region.Id),
            viaMs.TryGetValue(r.Region.Id, out var via) ? via : new Dictionary<string, double>(),
            directMs.GetValueOrDefault(r.Region.Id))).ToList();
        var order = profile.Relays.Where(r => r.ViaRelayId is null).Select(r => r.Id).ToList();
        var plan = RegionPlanner.Plan(homeRelayId, measurements, new PlannerOptions(game.RegionDirect, RegionRouting.MaxTunnels, order));

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var leaving = plan.Where(d => d.Path.Kind != PathKind.Home).ToList();
        _log($"Region plan for {game.Name} from {home.Name} [{home.Id}], {seconds:F0} s" +
             (stopped is null ? "" : $", incomplete - {stopped}") + ":");
        foreach (var decision in plan) _log($"  {decision.RegionId} -> {decision.Path}: {decision.Reason}");
        _log(leaving.Count == 0
            ? "  Every region stays on home. Nothing was moved."
            : $"  {leaving.Count} of {plan.Count} region(s) would leave home. Nothing was moved - this version records plans only.");

        if (_recorder is { } recorder)
        {
            var record = new RegionPlanRecord(
                DateTimeOffset.UtcNow,
                mode.ToString().ToLowerInvariant(),
                source,
                Acted: false,
                homeRelayId,
                game.RegionDirect,
                RegionRouting.MaxTunnels,
                seconds,
                stopped,
                relaysMeasured,
                plan.Select(d => new RegionPlanEntry(
                    d.RegionId,
                    measurements.First(m => m.RegionId == d.RegionId).HasLandmark,
                    homeMs.GetValueOrDefault(d.RegionId),
                    directMs.GetValueOrDefault(d.RegionId),
                    viaMs.TryGetValue(d.RegionId, out var v) ? v : new Dictionary<string, double>(),
                    viaWay.TryGetValue(d.RegionId, out var w) ? w : new Dictionary<string, string>(),
                    d.Path.ToString(),
                    d.ChosenMs,
                    d.Reason)).ToList());
            recorder.WriteRegionPlan(record, recorder.CurrentMeta());
        }
        return true;
    }

    /// <summary>
    /// One handshake down one way into a relay the tunnel is not on, or null - and a line in the log - when it
    /// does not answer. One attempt, as a rescan makes: a relay that does not answer inside two seconds is not
    /// one a region would be sent to.
    /// </summary>
    private async Task<TunnelClient?> HandshakeForPlanAsync(RelayEntry way, byte[] psk, CancellationToken ct)
    {
        TunnelClient? client = null;
        try
        {
            client = new TunnelClient(ParseEndpoint(way.Endpoint), AuthFor(way, psk), _clientId, _log);
            await client.HandshakeAsync(attempts: 1, ct).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _log($"  {way.Name} [{way.Id}]: unreachable - {ex.Message}");
            // As in MeasureCandidateAsync: an entry's session is its relay's.
            if (way.ViaRelayId is null) client?.Dispose();
            else Abandon(client);
            return null;
        }
    }

    /// <summary>The region's first landmark that is an IPv4 address, or null.</summary>
    private static IPAddress? FirstLandmark(RegionEntry region)
    {
        foreach (var text in region.Landmarks)
        {
            if (IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return address;
            }
        }
        return null;
    }

    private static string Ms(double? ms) => ms is { } value ? $"{value:F0} ms" : "no answer";
}
