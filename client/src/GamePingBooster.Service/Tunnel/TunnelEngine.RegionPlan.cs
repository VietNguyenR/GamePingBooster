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
/// In "record" NOTHING IS MOVED: the pass exists to find out, from real players on real lines, how often a region
/// would leave home and by how much. In "on" the plan is put in force by ApplyPlanAsync (TunnelEngine.MultiTunnel.cs),
/// and a relay that already has a tunnel open is measured through it rather than handshaken.
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

    /// <summary>Game UDP into the tunnels below this rate is the lobby - see <see cref="LobbyGate"/>.</summary>
    private const double LobbyPacketsPerSecond = LobbyGate.PacketsPerSecond;

    /// <summary>A pass interrupted by a match or a quiet tunnel is tried again, this many times per connection and game.</summary>
    private const int RegionPlanTries = 3;

    /// <summary>The home tunnel quiet this long mid-pass: stop and let the supervisor look at it.</summary>
    private static readonly TimeSpan RegionPlanHomeQuiet = TimeSpan.FromSeconds(5);

    /// <summary>Tries per (game, home relay); -1 once a pass completed. Supervisor only, reset per connect.</summary>
    private readonly Dictionary<string, int> _regionPlanTries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the game is in its lobby. Armed when its routes go in (InstallRoutes), asked by the supervisor.</summary>
    private readonly LobbyGate _lobby = new();

    /// <summary>The game's routes just went in: the lobby count starts now. See <see cref="LobbyGate.Arm"/>.</summary>
    private void ArmLobbyGate()
    {
        if (_tunnel is { } home) _lobby.Arm(home, NowMs(), AllGameUdpPackets(home));
    }

    /// <summary>
    /// The plan in force, for the next pass's hysteresis (planner rule 7): a region keeps its path unless another beats
    /// it by the margin. Only for the same game and home - a plan made from another home compared other numbers.
    /// Supervisor only.
    /// </summary>
    private (string GameId, string HomeRelayId, Dictionary<string, RegionPath> Paths)? _planInForce;

    private void ResetRegionPlanning()
    {
        _planInForce = null;
        _regionPlanTries.Clear();
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
            _lobby.Reset();
            return;
        }

        var (mode, source) = RegionRouting.Resolve(_config.RegionRouting, game.RegionRouting, game.LandmarksRouted);
        if (mode == RegionRoutingMode.Off) return;

        var key = $"{game.Id}|{RelayPaths.RelayIdOf(home)}";
        var tries = _regionPlanTries.GetValueOrDefault(key);
        if (tries < 0 || tries >= RegionPlanTries) return;

        // The lobby: no game UDP since the routes went in, or under the rate over two passes in a row.
        if (!_lobby.Observe(tunnel, NowMs(), AllGameUdpPackets(tunnel))) return;

        // One region is what connect already measured; a plan needs something to choose between.
        if (game.Regions.Count < 2 || !game.Regions.Any(r => FirstLandmark(r) is not null))
        {
            _regionPlanTries[key] = -1;
            _log($"Region plan ({mode.ToString().ToLowerInvariant()}, from {source}): {game.Name} has " +
                 (game.Regions.Count < 2 ? "one region" : "no region with a landmark") + " - nothing to plan.");
            return;
        }

        _regionPlanTries[key] = tries + 1;
        if (await PlanRegionsAsync(tunnel, game, home, profile, mode, source, ct).ConfigureAwait(false))
        {
            _regionPlanTries[key] = -1;
        }
        _lobby.Reset();
    }

    /// <summary>
    /// Measures, plans, logs and records. Returns false when the pass was interrupted in a way worth trying
    /// again - a match starting, the home tunnel going quiet - and true otherwise, including a pass the budget
    /// cut short, which is recorded as incomplete: a second try would only be cut short again.
    /// </summary>
    private async Task<bool> PlanRegionsAsync(TunnelClient tunnel, GameEntry game, RelayEntry home, ProfileBundle profile,
        RegionRoutingMode mode, string source, CancellationToken ct, string trigger = "connect")
    {
        var homeRelayId = RelayPaths.RelayIdOf(home);
        var started = Stopwatch.GetTimestamp();
        var packetsAtStart = AllGameUdpPackets(tunnel);
        var regions = game.Regions.Select(r => (Region: r, Landmark: FirstLandmark(r))).ToList();
        var measurable = regions.Where(r => r.Landmark is not null).Select(r => (r.Region, Landmark: r.Landmark!)).ToList();

        var homeMs = new Dictionary<string, double?>(StringComparer.Ordinal);
        var directMs = new Dictionary<string, double?>(StringComparer.Ordinal);
        var viaMs = measurable.ToDictionary(r => r.Region.Id, _ => new Dictionary<string, double>(StringComparer.Ordinal), StringComparer.Ordinal);
        var viaWay = measurable.ToDictionary(r => r.Region.Id, _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var relaysMeasured = new List<string>();
        string? stopped = null;
        var retry = false;   // only ever set to true, from any of the measuring tasks

        _log($"Region plan ({mode.ToString().ToLowerInvariant()}, from {source}): measuring {measurable.Count} of {regions.Count} regions of {game.Name} " +
             $"through {home.Name} [{home.Id}], over your own line and through the other relays - the median of " +
             $"{RescanScore.Samples} echoes each.");

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
            if (AllGameUdpPackets(tunnel) - packetsAtStart > 10 + LobbyPacketsPerSecond * elapsed)
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

            // Every other relay that carries the game, one way in at a time - and those that may carry a region of it
            // and nothing else: the profile's secondaryGames (/admin/relays) and this machine's config.json
            // (ServiceConfig.RegionRoutingRelays). Neither kind can become home - connect, failover and the relay list
            // only ever look at RelaysForGame.
            var others = RelaysForGame(game)
                .Where(r => r.ViaRelayId is null && !r.Id.Equals(homeRelayId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var extra in profile.Relays.Where(r => r.ViaRelayId is null && RelayPaths.SecondaryOnlyFor(r, game.Id)))
            {
                if (extra.Id.Equals(homeRelayId, StringComparison.OrdinalIgnoreCase) || others.Contains(extra)) continue;
                others.Add(extra);
                _log($"  {extra.Name} [{extra.Id}] is measured too - the profile lets it carry a region of {game.Name}, never home.");
            }
            foreach (var id in _config.RegionRoutingRelays ?? [])
            {
                var extra = profile.Relays.FirstOrDefault(r => r.ViaRelayId is null && r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (extra is null)
                {
                    _log($"  regionRoutingRelays names '{id}', which is not a relay in the profile - ignored.");
                    continue;
                }
                if (extra.Id.Equals(homeRelayId, StringComparison.OrdinalIgnoreCase) || others.Contains(extra)) continue;
                others.Add(extra);
                _log($"  {extra.Name} [{extra.Id}] is measured too - regionRoutingRelays in config.json; it can carry a region, never home.");
            }
            // In parallel across relays, in order within one: every way into ONE relay resumes the same session, so two
            // at once would take it from each other (G5); two different relays share nothing. Each echo is a few dozen
            // bytes, so six relays at once is a few dozen packets a second - nothing a line notices, in the lobby.
            // Home and the player's own line alongside the relays: three paths that share nothing but the PC's uplink.
            var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);
            var relayTasks = others.Select(relay => MeasureRelayForPlanAsync(relay, profile, measurable, psk, Interrupted, token)).ToList();
            var homeTask = MeasureHomeAndDirectAsync();
            var results = await Task.WhenAll(relayTasks).ConfigureAwait(false);
            var homeStopped = await homeTask.ConfigureAwait(false);

            _log("  " + string.Join(", ", measurable.Select(r =>
                $"{r.Region.Id}: home {Ms(homeMs.GetValueOrDefault(r.Region.Id))}, direct {Ms(directMs.GetValueOrDefault(r.Region.Id))}")));

            // Through home: the live tunnel, never a handshake (G5). Over the player's own line: a landmark inside a
            // routed range would be measured through the tunnel and called direct; the profile rules keep that from
            // happening, and this keeps it from counting. Home and direct run side by side, each in order.
            async Task<string?> MeasureHomeAndDirectAsync()
            {
                async Task<string?> Home()
                {
                    foreach (var (region, landmark) in measurable)
                    {
                        if (Interrupted() is { } why) return why;
                        var median = RescanScore.Median(await SampleLiveAsync(tunnel, landmark, token).ConfigureAwait(false));
                        lock (homeMs) homeMs[region.Id] = median;
                    }
                    return null;
                }
                async Task<string?> Direct()
                {
                    foreach (var (region, landmark) in measurable)
                    {
                        if (Interrupted() is { } why) return why;
                        double? median = IsRoutedIntoTunnel(landmark)
                            ? null
                            : RescanScore.Median(await LandmarkProbe.SampleAsync(
                                landmark, RescanScore.Samples, LiveSampleSpacing, ProbeTimeoutMs, token).ConfigureAwait(false));
                        lock (directMs) directMs[region.Id] = median;
                    }
                    return null;
                }
                try
                {
                    var both = await Task.WhenAll(Home(), Direct()).ConfigureAwait(false);
                    return both.FirstOrDefault(r => r is not null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return null;   // the budget: counted below, with what was measured kept
                }
            }

            foreach (var result in results)
            {
                foreach (var line in result.Lines) _log(line);
                if (result.Measured) relaysMeasured.Add(result.RelayId);
                foreach (var (regionId, (ms, way)) in result.Best)
                {
                    viaMs[regionId][result.RelayId] = ms;
                    viaWay[regionId][result.RelayId] = way;
                }
            }
            stopped = homeStopped ?? results.Select(r => r.Stopped).FirstOrDefault(r => r is not null);
            if (stopped is null && token.IsCancellationRequested) stopped = $"the {RegionPlanBudget.TotalSeconds:F0} s budget ran out";
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
        var previous = _planInForce is { } inForce && inForce.GameId == game.Id &&
                       inForce.HomeRelayId.Equals(homeRelayId, StringComparison.OrdinalIgnoreCase)
            ? inForce.Paths
            : null;
        var plan = RegionPlanner.Plan(homeRelayId, measurements, new PlannerOptions(game.RegionDirect, RegionRouting.MaxTunnels, order), previous);
        if (mode == RegionRoutingMode.On) plan = ForcedByConfig(plan, homeRelayId, profile, viaMs);

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var leaving = plan.Where(d => d.Path.Kind != PathKind.Home).ToList();
        _log($"Region plan for {game.Name} from {home.Name} [{home.Id}], {seconds:F0} s" +
             (stopped is null ? "" : $", incomplete - {stopped}") + ":");
        foreach (var decision in plan) _log($"  {decision.RegionId} -> {decision.Path}: {decision.Reason}");

        var acted = false;
        if (mode == RegionRoutingMode.On)
        {
            try
            {
                acted = await ApplyPlanAsync(tunnel, game, plan, viaWay, profile, ct).ConfigureAwait(false);
                if (acted) _planInForce = (game.Id, homeRelayId, plan.ToDictionary(d => d.RegionId, d => d.Path, StringComparer.Ordinal));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Region routing: the plan could not be put in force ({ex.Message}) - every region stays where it was.");
            }
        }
        else
        {
            _log(leaving.Count == 0
                ? "  Every region stays on home. Nothing was moved."
                : $"  {leaving.Count} of {plan.Count} region(s) would leave home. Nothing was moved - region routing is on record.");
        }

        if (_recorder is { } recorder)
        {
            var record = new RegionPlanRecord(
                DateTimeOffset.UtcNow,
                trigger,
                mode.ToString().ToLowerInvariant(),
                source,
                acted,
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
            recorder.WriteRegionPlan(record, recorder.CurrentMeta(SpikeContext(home: true)));
        }
        return true;
    }

    /// <summary>
    /// A match just ended and region routing is on: the regions are planned again before the next one - the answer
    /// to "the plan was measured once, at connect". Every relay for every region, relays with a tunnel open measured
    /// through it, the previous plan kept unless something beats it by the margin (hysteresis). Nothing in use moves:
    /// a server still talking keeps its tunnel whatever the new plan says, so applying it needs no timing guess.
    ///
    /// It replaces the between-matches rescan, which moved HOME for the one region connect guessed; with region
    /// routing on, home stays and each region gets its own relay instead. If the next match starts while this
    /// measures, the pass stops and the plan in force stays.
    /// </summary>
    private async Task ReplanBetweenMatchesAsync(TunnelClient tunnel, GameEntry game, CancellationToken ct)
    {
        var home = _relay;
        var profile = _profile;
        if (home is null || profile is null || !(_watcher?.IsGameRunning ?? false)) return;
        if (game.Regions.Count < 2 || !game.Regions.Any(r => FirstLandmark(r) is not null)) return;

        var (mode, source) = RegionRouting.Resolve(_config.RegionRouting, game.RegionRouting, game.LandmarksRouted);
        _log($"Between matches: the game has been silent {SilenceOfAll(tunnel).TotalSeconds:F0} s - planning {game.Name}'s regions again.");
        if (!await PlanRegionsAsync(tunnel, game, home, profile, mode, source, ct, trigger: "after-match").ConfigureAwait(false))
        {
            _log("Between matches: no new plan this time - the plan in force stays.");
        }
    }

    /// <summary>When the game last sent on any tunnel, on NowMs's clock; null if it never has.</summary>
    private long? LastSentAnyMs(TunnelClient home)
    {
        long? latest = LastSentMs(home);
        lock (_otherTunnels)
        {
            foreach (var other in _otherTunnels.Values)
            {
                if (LastSentMs(other.Client) is { } at && (latest is null || at > latest)) latest = at;
            }
        }
        return latest;
    }

    private TimeSpan SilenceOfAll(TunnelClient home) =>
        LastSentAnyMs(home) is { } at ? TimeSpan.FromMilliseconds(Math.Max(0, NowMs() - at)) : TimeSpan.MaxValue;

    /// <summary>What one relay measured for a plan: its best number and way per region, and its log lines in order.</summary>
    private sealed record RelayPlanResult(
        string RelayId,
        bool Measured,
        Dictionary<string, (double Ms, string Way)> Best,
        List<string> Lines,
        string? Stopped);

    /// <summary>
    /// One relay, every way into it one after another - see PlanRegionsAsync for why never two at once. A relay with a
    /// tunnel open is measured through it, never by a handshake: a second handshake would move that tunnel's session
    /// to the probe (G5). Returns what it measured before the budget or an interruption stopped it; never throws for
    /// either, so the other relays' numbers are kept.
    /// </summary>
    private async Task<RelayPlanResult> MeasureRelayForPlanAsync(RelayEntry relay, ProfileBundle profile,
        List<(RegionEntry Region, IPAddress Landmark)> measurable, byte[] psk, Func<string?> interrupted, CancellationToken token)
    {
        var best = new Dictionary<string, (double Ms, string Way)>(StringComparer.Ordinal);
        var lines = new List<string>();
        string? stopped = null;
        var measured = false;
        TunnelClient? open = null;
        try
        {
            if (OtherTunnelTo(relay.Id) is { } live)
            {
                // The way the open tunnel is on, which may be an entry: the record says which way gave the number,
                // and naming the relay here said "direct to the relay" for a tunnel that came in through an entry.
                var wayId = OtherTunnelOf(live)?.Way.Id ?? relay.Id;
                var liveLine = new List<string>();
                foreach (var (region, landmark) in measurable)
                {
                    if ((stopped = interrupted()) is not null) break;
                    var median = RescanScore.Median(await SampleLiveAsync(live, landmark, token).ConfigureAwait(false));
                    liveLine.Add($"{region.Id} {Ms(median)}");
                    if (median is { } ms) best[region.Id] = (ms, wayId);
                }
                lines.Add($"  {relay.Name} [{wayId}], open: {string.Join(", ", liveLine)}");
                return new RelayPlanResult(relay.Id, true, best, lines, stopped);
            }

            foreach (var way in RelayPaths.DoorsOf(profile.Relays, relay.Id))
            {
                if ((stopped = interrupted()) is not null) break;
                if (IsRoutedIntoTunnel(way))
                {
                    lines.Add($"  {way.Name} [{way.Id}]: skipped - its address is inside a routed game range.");
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
                    if ((stopped = interrupted()) is not null) break;
                    var samples = new List<double?>(RescanScore.Samples);
                    for (var i = 0; i < RescanScore.Samples; i++)
                    {
                        samples.Add(await open.MeasureThroughTunnelAsync(landmark, attempts: 1, token).ConfigureAwait(false));
                    }
                    var median = RescanScore.Median(samples);
                    line.Add($"{region.Id} {Ms(median)}");
                    if (median is { } ms && (!best.TryGetValue(region.Id, out var kept) || ms < kept.Ms)) best[region.Id] = (ms, way.Id);
                }
                lines.Add($"  {way.Name} [{way.Id}]: {string.Join(", ", line)}");
                measured = true;
                if (stopped is not null) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The budget, or the connection ending: what was measured so far still counts.
            lines.Add($"  {relay.Name} [{relay.Id}]: cut short by the budget.");
        }
        catch (Exception ex)
        {
            lines.Add($"  {relay.Name} [{relay.Id}]: could not be measured ({ex.Message}).");
        }
        finally
        {
            // With a Disconnect: the relay's session goes back to its pool now rather than in 90 s.
            open?.Dispose();
        }
        return new RelayPlanResult(relay.Id, measured, best, lines, stopped);
    }

    /// <summary>
    /// The plan with this machine's regionRoutingForce applied - see ServiceConfig.RegionRoutingForce. For testing
    /// only: it can give a region a slower path than home, so every forced region says so in its reason, which the
    /// log and the quality record both carry.
    /// </summary>
    private List<RegionDecision> ForcedByConfig(List<RegionDecision> plan, string homeRelayId, ProfileBundle profile,
        IReadOnlyDictionary<string, Dictionary<string, double>> viaMs)
    {
        var forced = _config.RegionRoutingForce;
        if (forced is null || forced.Count == 0) return plan;

        return [.. plan.Select(d =>
        {
            if (!forced.TryGetValue(d.RegionId, out var relayId) || string.IsNullOrWhiteSpace(relayId)) return d;
            var relay = profile.Relays.FirstOrDefault(r => r.ViaRelayId is null && r.Id.Equals(relayId, StringComparison.OrdinalIgnoreCase));
            if (relay is null || relay.Id.Equals(homeRelayId, StringComparison.OrdinalIgnoreCase))
            {
                _log($"  regionRoutingForce: {d.RegionId} -> '{relayId}' ignored - " +
                     (relay is null ? "no such relay in the profile." : "that is home."));
                return d;
            }
            double? ms = viaMs.TryGetValue(d.RegionId, out var via) && via.TryGetValue(relay.Id, out var v) ? v : null;
            var reason = $"FORCED by regionRoutingForce in config.json - {relay.Id} {(ms is { } x ? $"{x:F0} ms" : "not measured")}" +
                         (d.HomeMs is { } h ? $" against home {h:F0} ms" : "") +
                         (d.Path.Kind == PathKind.Relay ? $" (the planner chose relay {d.Path})" : $" (the planner chose {d.Path})");
            _log($"  {d.RegionId} -> {relay.Id}: {reason}");
            return d with { Path = RegionPath.Via(relay.Id), ChosenMs = ms, Reason = reason };
        })];
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
