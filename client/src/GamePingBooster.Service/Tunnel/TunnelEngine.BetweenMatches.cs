using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Network;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Moving to a faster relay in the gap between two matches.
///
/// Relays are measured once, at connect, and a connection lasts an evening. A road that was the best at
/// eight can be the worst at ten, and entry switching can only move between the ways into the SAME relay,
/// because another relay leaves through another address and the match in progress would drop. Between
/// matches there is no match to drop. So when a match ends (<see cref="MatchGap"/>), every other relay and
/// every entry in front of one is measured against the path in use, the same way and at the same moment,
/// and the tunnel moves to one that is faster by a clear margin (<see cref="RescanScore"/>).
///
/// What the move costs: the lobby's TCP connection, which leaves through the new relay's address and has to
/// be opened again. The owner disconnects and reconnects in the PUBG lobby daily and it comes back by itself.
///
/// THE WORST CASE is the next match starting while the tunnel is being swapped, and three things keep it
/// away. The move is made only if the tunnel has carried NO game UDP since the gap began, checked again right
/// before the swap. It is made only while the game has been silent for at most <see cref="MoveBefore"/>: the
/// shortest gap between two matches seen on 2026-09-18 was about 22 s, and most were 45 s or more. And the
/// game routes stay in place through the swap, so a packet sent in that half second waits in the adapter or
/// is lost, and the game sends it again through the new relay - rather than leaving over the player's own
/// connection with his own address and making the match server see the source change mid-handshake.
///
/// The ways into the relay in use are NOT measured here. A handshake down another way into the same relayd
/// moves the live session onto it, which is the one thing a probe must never do - see SelectRelayAsync.
/// Those are entry switching's job, and it compares them all match long.
/// </summary>
internal sealed partial class TunnelEngine
{
    private MatchGap _matchGap = new();

    /// <summary>The whole rescan, measuring and moving; past this it is dropped and the tunnel stays.</summary>
    private static readonly TimeSpan RescanBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest the game may have been silent when the tunnel is swapped. Past it the next match is too
    /// close to risk, and the move waits for the next gap.
    /// </summary>
    private static readonly TimeSpan MoveBefore = TimeSpan.FromSeconds(20);

    /// <summary>Between echoes down the live tunnel, so the eight are spread the way the candidates' are.</summary>
    private static readonly TimeSpan LiveSampleSpacing = TimeSpan.FromMilliseconds(100);

    /// <summary>How long after a move the next match is waited for before the record is written without one.</summary>
    private static readonly TimeSpan FollowNextMatchFor = TimeSpan.FromMinutes(10);

    /// <summary>How long into the next match the game must still be sending for the move to count as held.</summary>
    private static readonly TimeSpan HeldAfter = TimeSpan.FromSeconds(90);

    /// <summary>The move made between matches whose next match is still being watched. Supervisor only.</summary>
    private RelayMoveFollow? _moveFollow;

    private sealed class RelayMoveFollow
    {
        public required RelayMoveRecord Record { get; set; }
        public required QualityMeta Meta { get; init; }
        public required TunnelClient Tunnel { get; init; }
        public required long MovedAt { get; init; }
    }

    private static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>When the game last sent into this tunnel, on <see cref="NowMs"/>'s clock; null if it never has.</summary>
    private static long? LastSentMs(TunnelClient tunnel) =>
        tunnel.Destinations.LastUdpAt is var at and not 0 ? at * 1000 / Stopwatch.Frequency : null;

    private static TimeSpan SilenceOf(TunnelClient tunnel) =>
        tunnel.Destinations.LastUdpAt is var at and not 0 ? Stopwatch.GetElapsedTime(at) : TimeSpan.MaxValue;

    /// <summary>
    /// One supervisor pass: reads the gap, waits the last few seconds of one out so the rescan starts ten seconds
    /// after the game's last packet rather than up to fifteen, and runs it.
    /// </summary>
    private async Task WatchForMatchGapAsync(TunnelClient tunnel, CancellationToken ct)
    {
        FollowRelayMove();

        var due = _matchGap.Feed(NowMs(), tunnel.Destinations.UdpPackets, LastSentMs(tunnel));
        if (!due && _matchGap.DueInMs(NowMs()) is { } wait && wait < 5000)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
            if (!ReferenceEquals(tunnel, _tunnel) || _state != TunnelState.Connected) return;
            due = _matchGap.Feed(NowMs(), tunnel.Destinations.UdpPackets, LastSentMs(tunnel));
        }
        if (due) await RescanBetweenMatchesAsync(tunnel, ct).ConfigureAwait(false);
    }

    /// <summary>Set when the game now running is one the relay in use does not carry. Read by the supervisor.</summary>
    private volatile bool _moveOffForGame;

    /// <summary>
    /// Called by the supervisor, and only by it: when a match has just ended, or - <paramref name="forGame"/> -
    /// as soon as a game opens that the relay in use is not set to carry (Relay.games in /admin/relays). Measures,
    /// then moves or stays, and says which in the log either way.
    ///
    /// Leaving a relay not set for the game is not a matter of margins, or of the player's choice: the operator
    /// took that game off it, so the tunnel goes to the fastest relay that carries it, whatever it measures, and
    /// without waiting for a gap - the game has just opened, and no match has begun. A match already under way
    /// is still never moved; the next gap does it.
    /// </summary>
    private async Task RescanBetweenMatchesAsync(TunnelClient tunnel, CancellationToken ct, bool forGame = false)
    {
        var current = _relay;
        var profile = _profile;
        var path = _path;
        if (current is null || profile is null || _routes is null) return;
        forGame |= !RelayPaths.Serves(current, _game?.Id);
        if (!forGame && _config.RescanBetweenMatches == false) return;

        // A relay picked by hand is never left for another. Both moments count: the choice this tunnel was
        // connected with - switching the app to automatic mid-session applies from the next connect, as the
        // app says - and the choice now, since picking a relay mid-session says where the player wants to be.
        if (!forGame && (_chosenByHandAtConnect ?? RelayChoice) is { } chosen)
        {
            _log($"Between matches: staying on {current.Name} - {chosen} was chosen by hand, so relays are not re-measured.");
            return;
        }
        if (!(_watcher?.IsGameRunning ?? false)) return;

        var currentRelayId = RelayPaths.RelayIdOf(current);
        var others = RelaysForGame(_game)
            .Where(r => !r.Id.Equals(currentRelayId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count == 0)
        {
            if (forGame) _log($"No relay in the profile is set to carry {_game?.Name} - staying on {current.Name}.");
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var packetsAtStart = tunnel.Destinations.UdpPackets;
        var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);
        var probes = new List<RelayProbe>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RescanBudget);
        var token = budget.Token;

        try
        {
            // The game just switched to has no measured path yet - SwitchGame blanked it - so its region is
            // found the way a connect finds it.
            if (forGame && path is null && await ChooseTargetRegionAsync(token).ConfigureAwait(false) is { } target)
            {
                path = new PathMeasurement(target.RegionName, 0, target.Landmark);
            }
            // Staying is right for a rescan, which moves only to something measured faster. It is wrong when the relay
            // must be left: a game with no landmark - Apex, whose servers answer nothing - kept the player on a relay
            // the operator took off it (Da Nang, 2026-09-23). Leaving needs no second leg, only somewhere to go, so the
            // relays that carry the game are compared on the leg to the relay, as a connect does without a landmark.
            if (path is null && !forGame)
            {
                _log($"Between matches: the path through {current.Name} to the game's region was never measured, so " +
                     "there is nothing to compare a relay against - staying.");
                return;
            }

            List<double?> hereSamples;
            double hereMs;
            if (forGame)
            {
                _log(path is null
                    ? $"{_game?.Name} has no landmark to measure the way to its servers, so the relays that carry it are " +
                      $"compared on the leg to the relay, to leave {current.Name} [{current.Id}]."
                    : $"Measuring the relays that carry {_game?.Name} to {path.RegionName}, the median of " +
                      $"{RescanScore.Samples} echoes each, to leave {current.Name} [{current.Id}].");
                hereSamples = [];
                hereMs = double.PositiveInfinity;
            }
            else
            {
                // Not null: this is a rescan, and a rescan without a path returned above.
                var live = path!;
                _log($"Between matches: the game has been silent {SilenceOf(tunnel).TotalSeconds:F0} s - re-measuring the relays " +
                     $"to {live.RegionName} against {current.Name} [{current.Id}], the median of {RescanScore.Samples} echoes each.");
                hereSamples = await SampleLiveAsync(tunnel, live.Landmark, token).ConfigureAwait(false);
                if (RescanScore.Median(hereSamples) is not { } measured)
                {
                    _log($"Between matches: {current.Name} answered too few echoes to be compared - staying.");
                    return;
                }
                hereMs = measured;
                _log($"  {current.Name} [{current.Id}], in use: {hereMs:F0} ms to {live.RegionName}");
            }

            foreach (var relay in others)
            {
                foreach (var way in RelayPaths.DoorsOf(profile.Relays, relay.Id))
                {
                    if (MatchStarted(tunnel, packetsAtStart) || (!forGame && TooLate(tunnel, current))) return;
                    if (IsRoutedIntoTunnel(way))
                    {
                        _log($"  {way.Name} [{way.Id}]: skipped - its address is inside a routed game range, so a probe would go through the tunnel.");
                        continue;
                    }

                    // One path to a relay open at a time: the next way into it resumes the same session.
                    CloseProbesOf(probes, relay.Id);
                    if (await MeasureCandidateAsync(way, psk, path, token).ConfigureAwait(false) is { } probe) probes.Add(probe);
                }
            }

            var best = probes.Where(p => p.EndToEndMs is not null).MinBy(p => p.EndToEndMs!.Value);
            if (best is null || (!forGame && !RescanScore.WorthMoving(hereMs, best.EndToEndMs!.Value)))
            {
                _log(best is null
                    ? $"Between matches: no other relay could be measured - staying on {current.Name}."
                    : $"Between matches: staying on {current.Name} at {hereMs:F0} ms - the fastest other path, {best.Relay.Name} " +
                      $"[{best.Relay.Id}] at {best.EndToEndMs!.Value:F0} ms, is not faster by " +
                      $"{RelayPaths.HelpMargin(hereMs):F0} ms or more.");
                return;
            }

            if (MatchStarted(tunnel, packetsAtStart) || (!forGame && TooLate(tunnel, current))) return;
            if (!ReferenceEquals(tunnel, _tunnel) || _state != TunnelState.Connected || !(_watcher?.IsGameRunning ?? false)) return;

            var client = await OpenChosenAsync(best, probes, psk, token).ConfigureAwait(false);
            probes.Clear();

            // The last word before the swap: a match's first packets would have gone out by now.
            if (MatchStarted(tunnel, packetsAtStart) || (!forGame && TooLate(tunnel, current)))
            {
                client.Dispose();
                return;
            }

            var record = new RelayMoveRecord(
                forGame ? "game" : "rescan",
                DateTimeOffset.UtcNow, current.Id, best.Relay.Id,
                Stopwatch.GetElapsedTime(started).TotalSeconds, SilenceOf(tunnel).TotalSeconds,
                DoorSwitchPolicy.Stats([.. hereSamples.OfType<double>()], hereSamples.Count, hereSamples.Count(s => s is null)),
                new DoorStats(best.EndToEndMs, null, RescanScore.Samples, 0),
                null, null, "");
            var meta = _recorder?.CurrentMeta();
            if (await MoveToRelayAsync(tunnel, best, client, forGame ? null : hereMs, path, ct).ConfigureAwait(false) is { } moved &&
                meta is { } m)
            {
                _moveFollow = new RelayMoveFollow { Record = record, Meta = m, Tunnel = moved, MovedAt = Stopwatch.GetTimestamp() };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log($"Between matches: measuring took longer than {RescanBudget.TotalSeconds:F0} s - staying on {current.Name}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Between matches: could not re-measure the relays ({ex.Message}) - staying on {current.Name}.");
        }
        finally
        {
            // Everything not moved to goes with a Disconnect: each is a session on a relay this tunnel is not on.
            foreach (var probe in probes)
            {
                probe.Client?.Dispose();
                probe.Client = null;
            }
        }
    }

    /// <summary>True, and says so, once the game has sent UDP into the tunnel since the gap began.</summary>
    private bool MatchStarted(TunnelClient tunnel, long packetsAtStart)
    {
        if (tunnel.Destinations.UdpPackets == packetsAtStart) return false;
        _log("Between matches: the game started sending to a server - the next match is loading, so the tunnel stays where it is.");
        return true;
    }

    /// <summary>True, and says so, once the game has been silent too long for a move to be safe before the next match.</summary>
    private bool TooLate(TunnelClient tunnel, RelayEntry current)
    {
        var silence = SilenceOf(tunnel);
        if (silence <= MoveBefore) return false;
        _log($"Between matches: the game has been silent {silence.TotalSeconds:F0} s, and the next match can start any moment " +
             $"after {MoveBefore.TotalSeconds:F0} - staying on {current.Name} until the next gap.");
        return true;
    }

    /// <summary><see cref="RescanScore.Samples"/> echoes to the landmark down the live tunnel. Null for each unanswered.</summary>
    private async Task<List<double?>> SampleLiveAsync(TunnelClient tunnel, IPAddress landmark, CancellationToken ct)
    {
        var samples = new List<double?>(RescanScore.Samples);
        for (var i = 0; i < RescanScore.Samples; i++)
        {
            if (i > 0) await Task.Delay(LiveSampleSpacing, ct).ConfigureAwait(false);
            samples.Add(await tunnel.ProbeGameServerAsync(landmark, ProbeTimeoutMs, ct).ConfigureAwait(false));
        }
        return samples;
    }

    /// <summary>
    /// Handshakes with one relay or entry that the tunnel is not on and measures it as the live path was
    /// measured, or logs why it could not. The probe it returns holds that tunnel open, not pumping.
    ///
    /// One handshake attempt, not the two a connect allows: a relay that does not answer inside two seconds
    /// is not one to move to, and every second spent waiting on it brings the next match closer.
    /// </summary>
    private async Task<RelayProbe?> MeasureCandidateAsync(RelayEntry way, byte[] psk, PathMeasurement? path, CancellationToken ct)
    {
        TunnelClient? client = null;
        try
        {
            client = new TunnelClient(ParseEndpoint(way.Endpoint), AuthFor(way, psk), _clientId, _log);
            await client.HandshakeAsync(attempts: 1, ct).ConfigureAwait(false);
            var legOne = await client.MeasureRelayRttAsync(attempts: 3, ct).ConfigureAwait(false) ?? client.HandshakeRttMs;

            // No landmark: only a move off a relay not used for the game gets here, and it is scored on this leg alone -
            // every candidate the same way, so no relay wins by a number the others were not measured on.
            if (path is null)
            {
                _log($"  {way.Name} [{way.Id}]: {legOne:F0} ms to the relay");
                return new RelayProbe(way, legOne, legOne) { Client = client };
            }

            var samples = new List<double?>(RescanScore.Samples);
            for (var i = 0; i < RescanScore.Samples; i++)
            {
                samples.Add(await client.MeasureThroughTunnelAsync(path.Landmark, attempts: 1, ct).ConfigureAwait(false));
            }
            var median = RescanScore.Median(samples);

            _log(median is { } ms
                ? $"  {way.Name} [{way.Id}]: {legOne:F0} ms to the relay, {ms:F0} ms to {path.RegionName}"
                : $"  {way.Name} [{way.Id}]: {legOne:F0} ms to the relay, too few answers from {path.RegionName} to compare");
            return new RelayProbe(way, legOne, median) { Client = client };
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _log($"  {way.Name} [{way.Id}]: unreachable - {ex.Message}");
            // As in ProbeAsync: an entry's session is its relay's, and was closed on purpose a moment ago.
            if (way.ViaRelayId is null) client?.Dispose();
            else Abandon(client);
            return null;
        }
    }

    /// <summary>
    /// Replaces the live tunnel with <paramref name="client"/>, a handshaken tunnel to another relay. The same
    /// steps as a failover in ReconnectAsync, with one difference that matters: the game and lobby routes stay in
    /// place throughout - see the class summary. Returns the new tunnel, or null when the move failed and the
    /// reconnect path took over.
    /// </summary>
    private async Task<TunnelClient?> MoveToRelayAsync(TunnelClient old, RelayProbe chosen, TunnelClient client, double? wasMs,
        PathMeasurement? path, CancellationToken ct)
    {
        var adapter = _adapter;
        var routes = _routes;
        var previous = _relay;
        if (adapter is null || routes is null || previous is null)
        {
            client.Dispose();
            return null;
        }

        var target = chosen.Relay;
        var nowMs = chosen.EndToEndMs!.Value;
        var swapStarted = Stopwatch.GetTimestamp();
        try
        {
            LogGameDestinations(old);
            var previousIp = old.Session.ClientIp;

            // The new relay's /32 goes in BEFORE the old tunnel stops, while nothing depends on it yet. It
            // replaces the old relay's pin, which is harmless now: the old tunnel sends one Disconnect and no more.
            routes.PinRelayRoute(ParseEndpoint(target.Endpoint).Address);

            // With a Disconnect: this relay is being left, and its address goes back to its pool. The adapter's
            // reader is taken off it first, so what the game sends during the swap waits in the adapter and leaves
            // through the new relay; Dispose then stops the old downlink.
            StopUplink();
            _tunnel = null;
            old.Dispose();

            _relay = target;
            _path = path is null ? null : new PathMeasurement(path.RegionName, Math.Max(0, nowMs - chosen.LegOneMs), path.Landmark);
            ForgetDirectPing();
            _pendingDoorMove = null;
            _movedFromDoor = null;
            _connectLeftDoor = null;

            _tunnel = client;
            ResetThroughputBaseline();
            if (!client.Session.ClientIp.Equals(previousIp))
            {
                routes.ConfigureAdapter(adapter.InterfaceIndex, client.Session.ClientIp, prefixLength: 24, client.Session.Mtu);
            }
            StartTunnel(client, ct);
            var swapMs = Stopwatch.GetElapsedTime(swapStarted).TotalMilliseconds;

            // After the pumps, off the critical half second: the routes were never taken out, so this only puts
            // back one Windows dropped with the address, and pins the new relay's other ways in.
            routes.RestoreRoutes(adapter.InterfaceIndex);
            PinDoors();

            if (wasMs is not { } was)
            {
                var gameName = _game?.Name ?? "";
                FallBackToAutomatic(previous.Name, gameName);
                _log($"Moved from {previous.Name} [{previous.Id}], which is not used for {gameName}, to {target.Name} " +
                     $"[{target.Id}] in {swapMs:F0} ms - {nowMs:F0} ms {(path is null ? "to the relay" : $"to {path.RegionName}")}. " +
                     "The lobby reconnects on its own.");
                SetState(TunnelState.Connected, new StatusText("svc.movedForGame",
                    $"Connected to {target.Name} - {previous.Name} is not used for {gameName}",
                    target.Name, previous.Name, gameName));
                return client;
            }

            var saved = was - nowMs;
            _log($"Between matches: moved from {previous.Name} [{previous.Id}] to {target.Name} [{target.Id}] in {swapMs:F0} ms - " +
                 $"{nowMs:F0} ms against {was:F0} ms to {path!.RegionName}, {saved:F0} ms faster. The lobby reconnects on its own.");
            SetState(TunnelState.Connected, new StatusText("svc.rescanMoved",
                $"Connected to {target.Name} - moved from {previous.Name} between matches, {saved:F0} ms faster",
                target.Name, previous.Name, saved.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
            return client;
        }
        catch (Exception ex)
        {
            // Half moved is not a state to leave a player in. The reconnect path starts from nothing and knows
            // how to put everything back, on whichever relay answers first.
            _log($"Between matches: the move to {target.Name} failed part way ({ex.Message}) - reconnecting.");
            if (!ReferenceEquals(_tunnel, client)) client.Dispose();
            await ReconnectAsync(ct).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Watches the match after a move, once per supervisor pass, and writes the move's record when there is an
    /// answer: the game sent on the new relay and was still sending <see cref="HeldAfter"/> later ("held"), or
    /// stopped before then, or no match came within <see cref="FollowNextMatchFor"/>, or the tunnel went.
    /// </summary>
    private void FollowRelayMove(string? endedBy = null)
    {
        if (_moveFollow is not { } follow) return;

        var tally = follow.Tunnel.Destinations;
        var first = tally.FirstUdpAt;
        double? startedAfter = first != 0 ? Math.Max(0, (first - follow.MovedAt) / (double)Stopwatch.Frequency) : null;
        bool? held = null;

        if (endedBy is null && !ReferenceEquals(follow.Tunnel, _tunnel)) endedBy = "tunnel-replaced";
        if (endedBy is null && first != 0 && Stopwatch.GetElapsedTime(first) >= HeldAfter)
        {
            held = Stopwatch.GetElapsedTime(tally.LastUdpAt) < TimeSpan.FromSeconds(5);
            endedBy = held.Value ? "held" : "stopped";
        }
        if (endedBy is null && first == 0 && Stopwatch.GetElapsedTime(follow.MovedAt) >= FollowNextMatchFor) endedBy = "no-match";
        if (endedBy is null) return;

        _moveFollow = null;
        var record = follow.Record with { NextMatchAfterSeconds = startedAfter, StillSendingAfter90s = held, FollowEnded = endedBy };
        _recorder?.WriteRelayMove(record, follow.Meta);
        if (endedBy == "stopped")
        {
            _log($"Between matches: the first match after the move to {record.To} stopped sending within " +
                 $"{HeldAfter.TotalSeconds:F0} s - written to the quality file for a look.");
        }
    }

    /// <summary>
    /// The relays that may carry <paramref name="game"/>, as set in /admin/relays. Every relay when none is set for
    /// it: a profile that leaves a game with no relay at all is a mistake on the server, and one the player
    /// should not pay for with no connection - the log says so instead.
    /// </summary>
    private List<RelayEntry> RelaysForGame(GameEntry? game)
    {
        var all = _profile?.Relays ?? [];
        var serving = RelayPaths.ServingGame(all, game?.Id);
        if (serving.Count > 0 || all.Count == 0) return serving;
        _log($"WARNING: no relay in the profile is set to carry {game?.Name} - using all of them. Tick it on a relay in /admin/relays.");
        return all;
    }

    /// <summary>
    /// The game being played right now, which is the only thing that rules a relay out - in the main window's list,
    /// in the choice saved from it, and at connect. Null when no game in the profile is open.
    ///
    /// Until 2026-09-23 this fell back to the game the next connect would guess - the last one played - so a player
    /// who had last played PUBG found Hong Kong greyed out while picking a relay for VALORANT, and could only pick it
    /// with VALORANT already open. With no game open there is nothing to rule a relay out for: the choice stands,
    /// and when a game opens that the relay does not carry, the tunnel moves to one that does (LeaveRelayNotForGame).
    /// </summary>
    private GameEntry? GameForRelayList()
    {
        // Connected: the watcher already knows, and _game is the game it last saw start.
        if (_watcher is { } watcher) return watcher.IsGameRunning ? _game : null;

        if (_profile is not { Games.Count: > 0 } profile) return null;
        var running = GameProcessWatcher.FindRunning(profile.Games.SelectMany(g => g.ProcessNames));
        return running is null ? null : ProfileMerge.FindByProcess(profile.Games, running);
    }

    /// <summary>
    /// Whether a relay's address falls inside a range this connection routes into the tunnel. Nothing is pinned
    /// for a candidate while it is measured, so such a probe would travel inside the tunnel in use.
    /// </summary>
    private bool IsRoutedIntoTunnel(RelayEntry way)
    {
        if (_game is null || !IPEndPoint.TryParse(way.Endpoint, out var endpoint) ||
            endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var value = ToUInt32(endpoint.Address);
        foreach (var cidr in _game.Regions.SelectMany(r => r.Cidrs))
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) ||
                network.AddressFamily != AddressFamily.InterNetwork ||
                !int.TryParse(parts[1], out var bits) || bits is < 0 or > 32)
            {
                continue;
            }
            var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
            if ((value & mask) == (ToUInt32(network) & mask)) return true;
        }
        return false;
    }
}
