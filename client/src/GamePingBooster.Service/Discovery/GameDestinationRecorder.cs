using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// Finds game servers the profile does not cover yet, on whichever machine the game is played on.
///
/// It does what a person does with `./gpb capture` open: notices that a match is not going through
/// the tunnel - the packet counters do not move - and reads which address the game is flooding with
/// UDP. The first half costs nothing, because the tunnel already counts the game's UDP
/// (<see cref="Tunnel.GameServerTally.GamePackets"/>). Only the second half needs ETW, so ETW is read
/// only while it can find something. The session itself is held open from Connect to Disconnect and paused
/// in between (<see cref="HeldTrace"/>): a game's anti-cheat may refuse a new one while the game runs.
///
///   TUNNELLED  the tunnel carries the game's UDP. The match server is inside the profile's ranges
///              by construction, there is nothing to find, and ETW is paused. With a complete profile
///              this is almost every match. It ends after <see cref="TunnelQuiet"/> without match traffic
///              on the tunnel, not at the first quiet moment.
///   LISTENING  the game runs and the tunnel carries none of its UDP: a lobby, a loading screen,
///              or a match on a server the profile lacks. ETW is read. In a lobby the game sends
///              little, so this is cheap exactly when it runs longest.
///   RESTING    a new server was just reported. ETW is paused for <see cref="RestAfterReport"/>: the
///              match it belongs to cannot be moved onto the tunnel anyway (a changed source address
///              drops the game's connection). After the rest it listens again; the same server still
///              busy is the same match, and rests again without a second report. The rest is short
///              because nothing else can end it: that match never touches the tunnel, so leaving it
///              is invisible until listening resumes.
///
/// A new server is one address the game sent enough packets to within <see cref="Window"/>, outside
/// the profile's ranges, not one of its landmarks and not on this machine's own network. How many is
/// enough is the game's own business - <see cref="PacketsFor"/> - because games differ by more than
/// ten times: measured on PUBG, 2026-09-17, the match server took about 60 packets a second, 1,800 in
/// thirty, and the busiest datacentre probe took 27 in three minutes; World of Tanks, measured through
/// the tunnel on 2026-09-22, takes 9-16 a second and would never reach PUBG's number.
///
/// A busy address INSIDE the ranges while listening is logged as a known server, not reported. It
/// means a match left the tunnel for a server the profile already has - routes being reinstalled
/// after a reconnect, typically - and seeing that line is how "old server, correctly recognised"
/// can be checked in a real match.
///
/// A new server goes to <see cref="DiscoveryUploader"/>, which sends it to the licence server at once.
/// Nothing about it is written to disk, and the log shows only the first part of its address
/// ("20.*.*.*", see Destinations.Mask): the owner decided on 2026-09-17 that the player's machine never
/// shows a found server in full. Every decision is logged that way - a first production test that
/// logged nothing about its finds could not be diagnosed.
/// Disclosed in the privacy policy, and
/// under the same switch as connection quality: switched off, or with no licence server or token to
/// send with, discovery does not listen at all.
///
/// While it listens it also keeps the game's own datacentre probes (<see cref="ProbeTap"/>): when a match starts -
/// on the tunnel or off it - the probing of the minutes before is handed to <c>probeRound</c>, so what the game
/// measured when it chose that datacentre is on record beside the match. Only while listening, which is the
/// lobby; a probe sent while resting is not seen.
///
/// Start and stop come from the process watcher's thread and are queued onto one chain; the rest
/// runs on a two-second timer. ETW's consumer thread runs at BelowNormal, so under CPU pressure it
/// loses events rather than taking time from the tunnel's pumps.
/// </summary>
internal sealed class GameDestinationRecorder : IDisposable
{
    private const string SessionName = "GamePingBooster-Discovery";

    /// <summary>
    /// Packets to one address within <see cref="Window"/> that make it a server, for a game whose profile
    /// declares no rate of its own. 500 since 2026-09-17: the first real detection took 1,129 in 28 s -
    /// about 40 a second, not the 60 of the first capture - so 1,000 would have missed a match running a
    /// little slower. The busiest datacentre probe is 27 in three minutes, still twenty times below.
    ///
    /// It stays the default because it is right for every game measured so far except one - Apex is the
    /// reason it must not be lowered for everyone, its lobby measures eleven datacentres at 25-60 packets
    /// a second on the gameplay ports. A game quieter than this says so in its profile; see
    /// <see cref="GameEntry.DiscoveryPacketsPerSecond"/>.
    /// </summary>
    internal const int NewServerPackets = 500;

    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long listening stays off after a report, or after finding the reported server still busy.
    ///
    /// 60 s, down from 5 min on 2026-09-17. Five minutes suits a PUBG match, but it was a clock and
    /// nothing more: a player who left that match - for the lobby, or straight into another game's
    /// match, or another game whose matches are shorter - waited out the whole of it before discovery
    /// could see the next one. A minute bounds that wait. Inside a long match it costs a check a
    /// minute of about fifteen seconds of ETW, at the few dozen events a second a match produces.
    /// </summary>
    internal static readonly TimeSpan RestAfterReport = TimeSpan.FromSeconds(60);

    /// <summary>How long listening stays off after the machine-wide event guard trips. Kept at five
    /// minutes: a download that floods UDP does not end in a minute, and this rest is about cost.</summary>
    private static readonly TimeSpan RestAfterGuard = TimeSpan.FromMinutes(5);

    /// <summary>Game UDP a second into the tunnel, averaged over <see cref="TunnelWindow"/>, that means
    /// a match is on it. A routed PUBG match is about 60; a lobby is 0.</summary>
    private const double TunnelCarryingPerSecond = 10;

    private static readonly TimeSpan TunnelWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the tunnel must carry no MATCH traffic - under <see cref="TunnelCarryingPerSecond"/> -
    /// before a match on it counts as over. Longer than <see cref="TunnelWindow"/> on purpose: a PUBG
    /// session hands the player from server to server - three of them in five minutes on 2026-09-17,
    /// each with a quiet load in between - and with the ten-second window alone listening went on and
    /// off three times in one session.
    ///
    /// 15 s, down from 30 s on 2026-09-17 because leaving a match took too long to reach listening
    /// again. The wait could also run past thirty seconds, for a second reason fixed at the
    /// same time: ANY game UDP into the tunnel restarted it, so the trickle a result screen or lobby
    /// sends kept a finished match "on the tunnel". Only a match's rate counts now. A load longer than
    /// fifteen seconds turns listening on for a moment, which costs one ETW session start and cannot
    /// report anything: the match's server is inside the ranges, and the tunnel takes it back within
    /// seconds of play resuming.
    /// </summary>
    private static readonly TimeSpan TunnelQuiet = TimeSpan.FromSeconds(15);

    /// <summary>UDP events a second, machine-wide, above which listening stops and rests.</summary>
    private const double MaxEventsPerSecond = 25_000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<long?> _tunnelGamePackets;
    private readonly Func<IPAddress?> _tunnelAddress;
    private readonly Func<bool> _enabled;
    private readonly Func<string?> _whyDisabled;
    private readonly Action<DiscoveredServer> _report;
    private readonly Action<GameEntry, ProbeRound, string>? _probeRound;
    private readonly Action<string> _log;
    private readonly HeldTrace _held;
    private readonly object _gate = new();
    private Task _chain = Task.CompletedTask;
    private Watch? _current;
    private bool _disposed;

    /// <param name="tunnelGamePackets">The live tunnel's <see cref="Tunnel.GameServerTally.GamePackets"/>,
    /// or null while there is no tunnel. A new tunnel starts again from zero; that is handled.</param>
    /// <param name="whyDisabled">Null when a finding could be sent, otherwise why not. Read every poll;
    /// non-null stops listening.</param>
    /// <param name="report">Where a new server goes. Called on the poll timer's thread.</param>
    /// <param name="probeRound">Where the game's probing before a match goes, with "tunnel" or "off-tunnel" for
    /// where that match went. Called on the poll timer's thread. Null keeps none.</param>
    /// <param name="tunnelAddress">The tunnel's inner address, or null while there is none: a TCP game's connection
    /// opened from it rides the tunnel and is not a candidate.</param>
    public GameDestinationRecorder(Func<long?> tunnelGamePackets, Func<string?> whyDisabled, Action<DiscoveredServer> report,
        Action<string> log, Action<GameEntry, ProbeRound, string>? probeRound = null, Func<IPAddress?>? tunnelAddress = null)
    {
        _probeRound = probeRound;
        _tunnelAddress = tunnelAddress ?? (() => null);
        _tunnelGamePackets = tunnelGamePackets;
        _whyDisabled = whyDisabled;
        _enabled = () => whyDisabled() is null;
        _report = report;
        _log = log;
        _held = new HeldTrace(SessionName, log);
    }

    /// <summary>
    /// Connect: opens the ETW session now, before any game starts, and holds it until <see cref="Close"/> - see
    /// <see cref="HeldTrace"/> for why. Queued, so Connect never waits on the self-test. Not while sending is off:
    /// then a listen opens it later, as before.
    /// </summary>
    public void Open() => Enqueue(() =>
    {
        if (!_enabled()) return;
        if (!_held.Open("connected")) _log($"Discovery: no ETW session for this connection - {_held.Failure}.");
    });

    /// <summary>Disconnect: stops the held session, or keeps retrying while a game's anti-cheat refuses.</summary>
    public void Close() => Enqueue(_held.Close);

    /// <summary>
    /// Packets within <see cref="Window"/> that make an address a server of <paramref name="game"/>:
    /// the rate its profile declares - already brought inside safe bounds by
    /// <see cref="GameEntry.DiscoveryRate"/> - or <see cref="NewServerPackets"/> when it declares none.
    ///
    /// Rounded up, so a declared rate is never quietly made easier to reach than it reads.
    /// </summary>
    internal static int PacketsFor(GameEntry game) => game.DiscoveryRate is { } rate
        ? (int)Math.Ceiling(rate * Window.TotalSeconds)
        : game.TcpGameplay ? (int)Math.Ceiling(TcpServerRate * Window.TotalSeconds) : NewServerPackets;

    /// <summary>
    /// Data segments a second sent to one address that make it a server of a TCP game declaring no rate of its own:
    /// <see cref="GameEntry.MinDiscoveryRate"/>, 90 in the window. Not the UDP default - TCP counts data segments, not
    /// packets, and a TCP game's match is far quieter than a UDP shooter's: Aniimo's took 9,123 packets both ways in
    /// 271 s (2026-10-13, about 17 a second each way), its gateways on TCP 10000 under half a packet a second. Web
    /// ports are not counted at all (TcpFlowSampler), so the floor only has to sit above the gateways.
    /// </summary>
    internal const double TcpServerRate = GameEntry.MinDiscoveryRate;

    /// <summary>A game from the profile is running. Does nothing if that game is already watched.</summary>
    public void GameStarted(GameEntry game) => Enqueue(() =>
    {
        if (_current is not null && _current.Game.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase)) return;
        StopCurrent();
        _current = new Watch(game, this);
        _current.Start();
    });

    /// <summary>The game exited, or the tunnel is being torn down.</summary>
    public void GameStopped() => Enqueue(StopCurrent);

    public void Dispose()
    {
        Task chain;
        lock (_gate)
        {
            if (_disposed) return;
            Enqueue(StopCurrent);
            Enqueue(_held.Dispose);
            _disposed = true;
            chain = _chain;
        }
        // Short: this runs after the engine's teardown inside the service's ten-second stop, and a
        // session it cannot stop in time is stopped as stale on the next start.
        chain.Wait(TimeSpan.FromSeconds(3));
    }

    private void Enqueue(Action action)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _chain = _chain.ContinueWith(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // Discovery must never take anything else down with it.
                    _log($"Discovery: {ex.Message}");
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void StopCurrent()
    {
        var watch = _current;
        if (watch is null) return;
        _current = null;
        watch.Stop();
    }

    private enum Phase { Starting, Tunnelled, Listening, Resting }

    /// <summary>One game session: from the game being detected to it exiting.</summary>
    private sealed class Watch(GameEntry game, GameDestinationRecorder owner)
    {
        private readonly object _gate = new();
        private readonly Action<string> _log = owner._log;
        private readonly string[] _processNames = game.ProcessNames
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p).ToArray();
        private readonly List<IPNetwork> _ranges = game.Regions.SelectMany(r => r.Cidrs)
            .Select(c => IPNetwork.TryParse(c, out var n) ? n : (IPNetwork?)null)
            .Where(n => n is not null).Select(n => n!.Value).ToList();
        private readonly HashSet<IPAddress> _landmarks = game.Regions.SelectMany(r => r.Landmarks)
            .Select(l => IPAddress.TryParse(l, out var a) ? a : null)
            .Where(a => a is not null).Select(a => a!).ToHashSet();

        /// <summary>
        /// The game's probes to its landmarks, for <see cref="EmitProbes"/>. None for a game without landmarks, or
        /// whose landmarks are routed (CS2): there the "landmark" is the match server itself, not a probe endpoint.
        /// </summary>
        private readonly ProbeTap? _probes = owner._probeRound is null || game.LandmarksRouted
            ? null
            : new ProbeTap(game.Regions.Select(r => (r.Id, (IReadOnlyList<string>)r.Landmarks))) is { HasLandmarks: true } tap ? tap : null;

        /// <summary>Settled once from the profile, so every comparison in this session uses one number
        /// and the log line that names it cannot drift from the one that decides.</summary>
        private readonly int _newServerPackets = PacketsFor(game);

        /// <summary>The game's TCP connections, counted per address - only for a game that plays over TCP.</summary>
        private readonly TcpFlowSampler? _tcp = game.TcpGameplay
            ? new TcpFlowSampler(game.LobbyAddresses.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
                .Where(a => a is not null).Select(a => a!).ToHashSet(), owner._tunnelAddress, owner._log)
            : null;

        private string Traffic => _tcp is null ? "UDP" : "match traffic";

        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly HashSet<uint> _gamePids = [];
        private readonly Queue<(long At, long? Packets)> _tunnelSamples = new();
        private readonly Queue<(long At, Dictionary<IPAddress, long> Sent)> _windows = new();
        private readonly HashSet<IPAddress> _reported = [];
        private readonly HashSet<IPAddress> _knownLogged = [];
        private readonly HashSet<IPAddress> _stillBusyLogged = [];

        // The reported server a rest is waiting on, while the checks keep finding it busy. Those checks
        // start and stop listening once a minute for a whole match; they log nothing until it goes quiet.
        private IPAddress? _waitingOn;

        private Timer? _timer;
        private int _polling;
        private bool _stopped;
        private Phase _phase = Phase.Starting;
        private long _restUntil;
        private long _lastTunnelGameAt;
        private bool _etwUnavailable;
        private bool _disabledLogged;
        private int _reports;

        // The table the held ETW session delivers into while listening, and its counters when it was attached.
        private FlowTable? _table;
        private long _attachedUdp;
        private long _attachedLost;
        private long _listenStarted;
        private long _guardAt;
        private long _guardEvents;

        // Totals over the whole game session, for the closing log line.
        private int _listens;
        private double _listenSeconds;
        private long _udpEvents;
        private long _eventsLost;

        public GameEntry Game { get; } = game;

        public void Start()
        {
            var declared = Game.DiscoveryPacketsPerSecond is { } rate
                ? $"{rate:0.#} packets/s declared for this game"
                : "the default rate";
            _log($"Discovery: watching {Game.Name} - {_ranges.Count} range(s) and {_landmarks.Count} landmark(s) " +
                 $"in the profile. A new server is {_newServerPackets:N0} packets to one address in " +
                 $"{Window.TotalSeconds:F0} s ({declared}{(_tcp is null ? "" : "; UDP packets or TCP data segments, web ports left out")}). " +
                 $"Listening only while the tunnel carries none of its {Traffic}.");
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }

        public void Stop()
        {
            if (_timer is { } timer)
            {
                using var done = new ManualResetEvent(false);
                if (timer.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(5));
            }

            lock (_gate)
            {
                if (_stopped) return;
                if (_phase == Phase.Listening) Evaluate(Stopwatch.GetTimestamp());
                StopListening(null);
                _stopped = true;

                var minutes = Stopwatch.GetElapsedTime(_started).TotalMinutes;
                _log($"Discovery: {Game.Name} closed after {minutes:F0} min. Listened {_listens} time(s), " +
                     $"{_listenSeconds:F0} s in all, {_udpEvents:N0} UDP events, lost {_eventsLost:N0}" +
                     (_tcp is null ? "" : $", {_tcp.Addresses} TCP address(es) counted") + "; " +
                     $"{_reports} new server(s) reported, {_knownLogged.Count} known server(s) seen off the tunnel.");
            }
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _polling, 1) != 0) return;
            try
            {
                lock (_gate)
                {
                    if (_stopped) return;
                    Step();
                }
            }
            catch (Exception ex)
            {
                _log($"Discovery: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _polling, 0);
            }
        }

        private void Step()
        {
            var now = Stopwatch.GetTimestamp();

            // Nothing found could be sent: do not spend a single event finding it.
            if (!owner._enabled())
            {
                if (!_disabledLogged)
                {
                    _disabledLogged = true;
                    _log($"Discovery: not listening - {owner._whyDisabled() ?? "sending is not possible"}.");
                }
                if (_phase == Phase.Listening) StopListening(null);
                return;
            }
            if (_disabledLogged)
            {
                _disabledLogged = false;
                _log("Discovery: sending is possible again.");
            }

            RefreshPids();

            var carrying = TunnelRate(now) is { } rate && rate >= TunnelCarryingPerSecond;
            if (carrying)
            {
                if (_phase != Phase.Tunnelled)
                {
                    StopListening(null);
                    _log($"Discovery: the tunnel is carrying {Game.Name}'s {Traffic} ({TunnelRate(now):N0} packets/s) - " +
                         "the match is on a server the profile covers. Not listening.");
                    _phase = Phase.Tunnelled;
                    _waitingOn = null;   // whatever match a rest was waiting on is over: this one is on the tunnel
                    // Seen up to TunnelWindow after the match began; the round says how long before this its probes ended.
                    EmitProbes("tunnel", now);
                }
                return;
            }

            // Between two servers of one match on the tunnel, not after it.
            if (_phase == Phase.Tunnelled && now - _lastTunnelGameAt < Ticks(TunnelQuiet)) return;

            if (_phase == Phase.Resting && now < _restUntil) return;

            if (_phase != Phase.Listening)
            {
                if (_etwUnavailable && _tcp is null) return;
                StartListening(now);
                return;
            }

            Evaluate(now);
            Guard(now);
        }

        /// <summary>Game UDP per second into the tunnel over the last <see cref="TunnelWindow"/>, or null
        /// when there is no tunnel or not enough of a window yet.</summary>
        private double? TunnelRate(long now)
        {
            if (_tunnelSamples.Count == 0 || _tunnelSamples.Last().At != now)
            {
                var current = owner._tunnelGamePackets();
                // A match's rate since the previous poll, not any packet at all - see TunnelQuiet.
                if (_tunnelSamples.Count > 0 && _tunnelSamples.Last() is { Packets: { } previous } last && current is { } counted)
                {
                    var elapsed = (now - last.At) / (double)Stopwatch.Frequency;
                    if (elapsed > 0 && (counted - previous) / elapsed >= TunnelCarryingPerSecond) _lastTunnelGameAt = now;
                }
                _tunnelSamples.Enqueue((now, current));
            }
            var horizon = now - Ticks(TunnelWindow);
            while (_tunnelSamples.Count > 1 && _tunnelSamples.ElementAt(1).At <= horizon) _tunnelSamples.Dequeue();

            var (oldAt, oldPackets) = _tunnelSamples.Peek();
            var latest = _tunnelSamples.Last().Packets;
            var seconds = (now - oldAt) / (double)Stopwatch.Frequency;
            if (latest is null || oldPackets is null || seconds < TunnelWindow.TotalSeconds * 0.6) return null;

            // A reconnect replaces the tunnel and its counter starts again from zero.
            var packets = latest.Value - oldPackets.Value;
            if (packets < 0)
            {
                _tunnelSamples.Clear();
                return null;
            }
            return packets / seconds;
        }

        private void RefreshPids()
        {
            foreach (var name in _processNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    _gamePids.Add((uint)process.Id);
                    process.Dispose();
                }
            }
        }

        private void StartListening(long now)
        {
            if (!_etwUnavailable && !StartTrace()) return;

            _tcp?.Clear();
            _listenStarted = now;
            _guardAt = now;
            _guardEvents = owner._held.ReadEvents;
            _windows.Clear();
            _windows.Enqueue((now, []));
            _listens++;
            _phase = Phase.Listening;

            if (_waitingOn is null)
            {
                _log($"Discovery: the tunnel carries none of {Game.Name}'s {Traffic} - listening" +
                     (_table is null ? " (TCP only, ETW is off for this session)."
                         : owner._held.EventIdFilterApplied ? "." : " (Windows refused the event-id filter, so TCP events are delivered and dropped)."));
            }
        }

        /// <summary>Starts the UDP half on the held session, opening it if Connect could not. False when it cannot run
        /// and nothing is left to listen with; a TCP game goes on listening to its TCP without it.</summary>
        private bool StartTrace()
        {
            var held = owner._held;
            if (!held.Open("needed by " + Game.Name))
            {
                // Any failure, not only the ones ETW documents: discovery is an extra, and a machine where
                // it cannot start must go on exactly as it did before discovery existed.
                _etwUnavailable = true;
                _log(_tcp is null
                    ? $"Discovery is off for this {Game.Name} session: {held.Failure}."
                    : $"Discovery listens to {Game.Name}'s TCP only this session - no ETW: {held.Failure}.");
                return _tcp is not null;
            }

            var table = new FlowTable((uint)Environment.ProcessId);
            table.SetGamePids(_gamePids);
            table.Probes = _probes;
            _attachedUdp = held.UdpEvents;
            _attachedLost = held.EventsLost;
            if (!held.Attach(table))
            {
                _etwUnavailable = true;
                _log($"Discovery: the ETW session is closing - {Game.Name} listens without it this session.");
                return _tcp is not null;
            }
            _table = table;
            return true;
        }

        /// <param name="rest">Rest this long before listening again; null leaves the phase to the caller.</param>
        private void StopListening(TimeSpan? rest)
        {
            if (_table is not null)
            {
                // Paused, not stopped: the session is held for the whole connection - see HeldTrace.
                var held = owner._held;
                held.Detach();
                _udpEvents += held.UdpEvents - _attachedUdp;
                _eventsLost += Math.Max(0, held.EventsLost - _attachedLost);
            }
            if (_phase == Phase.Listening) _listenSeconds += Stopwatch.GetElapsedTime(_listenStarted).TotalSeconds;
            _table = null;
            _tcp?.Clear();
            _windows.Clear();

            if (rest is { } span)
            {
                _phase = Phase.Resting;
                _restUntil = Stopwatch.GetTimestamp() + Ticks(span);
            }
            else if (_phase == Phase.Listening)
            {
                _phase = Phase.Starting;
            }
        }

        /// <summary>Looks for one address that took this game's threshold of packets within the window.</summary>
        private void Evaluate(long now)
        {
            if (_table is null && _tcp is null) return;

            var rows = new List<FlowRow>();
            if (_table is { } table)
            {
                table.SetGamePids(_gamePids);
                table.RemoveAllExcept(_gamePids);
                rows.AddRange(table.Snapshot(_gamePids).Where(r => !Destinations.IsLocal(r.Address)));
            }
            if (_tcp is not null) rows = Merge(rows, _tcp.Sample(_gamePids));
            var sent = rows.ToDictionary(r => r.Address, r => r.Sent);
            var horizon = now - Ticks(Window);
            while (_windows.Count > 1 && _windows.Peek().At < horizon) _windows.Dequeue();
            var (baseAt, baseline) = _windows.Peek();
            _windows.Enqueue((now, sent));
            var seconds = (now - baseAt) / (double)Stopwatch.Frequency;

            // A check on the reported server that has listened a whole window without finding it busy:
            // that match is over, and this listening is for the next one.
            if (_waitingOn is { } waited && seconds >= Window.TotalSeconds * 0.8 &&
                (sent.GetValueOrDefault(waited) - baseline.GetValueOrDefault(waited)) < _newServerPackets)
            {
                _log($"Discovery: {Destinations.Mask(waited)} went quiet - that match is over. Listening for the next one.");
                _waitingOn = null;
            }

            foreach (var row in rows.OrderByDescending(r => r.Sent))
            {
                var packets = row.Sent - baseline.GetValueOrDefault(row.Address);
                if (packets < _newServerPackets) continue;
                if (_landmarks.Contains(row.Address)) continue;

                if (_ranges.Any(r => r.Contains(row.Address)))
                {
                    // Only once the whole window says so. A match loading onto the tunnel is seen here
                    // for the few seconds before the tunnel's rate catches up, and a fast game can put
                    // a thousand packets into those seconds; that is a match on the tunnel, not off it.
                    if (seconds >= Window.TotalSeconds * 0.8 && _knownLogged.Add(row.Address))
                    {
                        EmitProbes("off-tunnel", row.FirstTimestamp);
                        _log($"Discovery: KNOWN server {Destinations.Mask(row.Address)} - {packets:N0} packets in {seconds:F0} s, " +
                             "inside the profile's ranges but not on the tunnel. Not reported; if the tunnel was up with " +
                             "routes in place, that is worth a look.");
                    }
                    continue;
                }

                var masked = Destinations.Mask(row.Address);
                if (!_reported.Add(row.Address))
                {
                    // Once per address. With a one-minute rest a long match would otherwise repeat this
                    // line every minute to the end.
                    _waitingOn = row.Address;
                    if (_stillBusyLogged.Add(row.Address))
                    {
                        _log($"Discovery: {masked} is still busy ({packets:N0} packets in {seconds:F0} s) - the same match, " +
                             $"not reported again. Checking again every {Describe(RestAfterReport)} until it goes quiet.");
                    }
                }
                else if (row.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    _log($"Discovery: NEW server {masked} - {packets:N0} packets in {seconds:F0} s, but IPv6, which the " +
                         $"profiles do not carry; not reported. Not listening for {Describe(RestAfterReport)}.");
                }
                else
                {
                    _reports++;
                    _waitingOn = row.Address;
                    EmitProbes("off-tunnel", row.FirstTimestamp);
                    _log($"Discovery: NEW server {masked} - {packets:N0} packets in {seconds:F0} s, outside the profile's " +
                         $"ranges. Reporting it; not listening for {Describe(RestAfterReport)}.");
                    owner._report(new DiscoveredServer(
                        Guid.NewGuid().ToString("N"), Game.Id, row.Address.ToString(),
                        row.Ports.Take(8).ToList(), packets, seconds, DateTimeOffset.UtcNow));
                }
                StopListening(RestAfterReport);
                return;
            }
        }

        /// <summary>UDP and TCP rows for one address become one: a server is busy by what the game sends it, either way.
        /// New rows, so neither source's running totals are changed.</summary>
        private static List<FlowRow> Merge(List<FlowRow> udp, List<FlowRow> tcp)
        {
            if (tcp.Count == 0) return udp;
            var merged = udp.ToDictionary(r => r.Address);
            foreach (var row in tcp)
            {
                if (!merged.TryGetValue(row.Address, out var into))
                {
                    merged[row.Address] = row;
                    continue;
                }
                var both = new FlowRow
                {
                    Address = row.Address, Sent = into.Sent + row.Sent,
                    FirstTimestamp = Math.Min(into.FirstTimestamp, row.FirstTimestamp),
                    LastTimestamp = Math.Max(into.LastTimestamp, row.LastTimestamp),
                };
                both.Ports.UnionWith(into.Ports);
                both.Ports.UnionWith(row.Ports);
                merged[row.Address] = both;
            }
            return [.. merged.Values];
        }

        /// <summary>
        /// Hands the probing before a match that began at <paramref name="matchAt"/> (QPC) to the owner, and logs it
        /// with addresses masked. Nothing when the game probed no landmark in the window before it.
        /// </summary>
        private void EmitProbes(string matchOn, long matchAt)
        {
            if (_probes is null || owner._probeRound is not { } emit) return;
            var round = _probes.Take(matchAt, owner._held.Rules?.ReceiveRemoteIsDestination is not null);
            if (round is null) return;

            var parts = round.Endpoints.Select(e =>
                $"{e.Region ?? "unmapped " + Destinations.Mask(e.Address)}: {e.Answered}/{e.Sent}" +
                (e.MedianMs is { } median ? $" {median:F0} ms" : ""));
            _log($"Discovery: {Game.Name}'s own probes before this match ({matchOn}), last " +
                 $"{round.FirstSecondsBeforeMatch:F0} s: {string.Join(", ", parts)}" +
                 (round.ReceivesVerified ? "." : " - receives unverified on this Windows, sends only."));
            emit(Game, round, matchOn);
        }

        private void Guard(long now)
        {
            if (_table is null) return;
            var seconds = (now - _guardAt) / (double)Stopwatch.Frequency;
            if (seconds < 10) return;

            // Every event read, not only UDP: where Windows refused the event-id filter, a TCP download floods the
            // session with events that are read and thrown away, and counting UDP alone never saw it.
            var events = owner._held.ReadEvents;
            var rate = (events - _guardEvents) / seconds;
            _guardEvents = events;
            _guardAt = now;
            if (rate <= MaxEventsPerSecond) return;

            // Paused, not closed: a closed session may not open again while a game's anti-cheat runs.
            _log($"Discovery: {rate:N0} ETW events/s on this machine, over the {MaxEventsPerSecond:N0} limit - " +
                 $"stopped listening for {Describe(RestAfterGuard)}.");
            StopListening(RestAfterGuard);
        }

        private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

        private static string Describe(TimeSpan span) =>
            span.TotalSeconds >= 120 ? $"{span.TotalMinutes:F0} min" : $"{span.TotalSeconds:F0} s";
    }
}
