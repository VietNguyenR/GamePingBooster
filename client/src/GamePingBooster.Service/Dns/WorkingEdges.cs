using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using GamePingBooster.Core.Net;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// For a scoped name, the addresses that were watched to work on THIS line - not the ones an
/// upstream resolver happened to name first.
///
/// Why this exists: encrypted DNS fixed the poisoning, and the Steam store was still slow. The
/// reason was not DNS at all. Some Akamai edges sit behind a middlebox that reads the server name
/// out of the TLS hello and resets Steam's, and 1.1.1.1 had handed out one of those for
/// store.steampowered.com while giving a clean edge for steamcommunity.com. One name was instant
/// and the other took nineteen seconds, from the same working resolver. See <see cref="EdgeProber"/>
/// for the measurement.
///
/// So for these few names the resolver stops relaying an answer and starts answering: ask every
/// upstream, pool what they say, handshake against each candidate, and reply with the survivors.
///
/// WHAT THIS GIVES UP, deliberately: for a scoped A query the reply is now synthesised, so EDNS
/// options, DNSSEC records and anything else in the upstream's message are dropped. That is a real
/// cost and it is why it is confined to A records for a handful of names - every other question,
/// including AAAA and HTTPS records for those same names, still travels through unread.
/// </summary>
internal sealed class WorkingEdges
{
    /// <summary>
    /// How long a verdict is trusted, and the TTL handed to clients.
    ///
    /// Two minutes is a compromise between two costs that pull opposite ways: probing again is
    /// three handshakes, and an edge that goes bad stays in the answer until this expires. It is
    /// short enough that a player who starts seeing the store hang is fixed within two minutes
    /// without touching anything.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public const uint AnswerTtlSeconds = 120;

    /// <summary>
    /// How long "no edge completed a handshake" is trusted before probing the name again.
    ///
    /// Longer than <see cref="Lifetime"/> because almost every name that lands here is not a web
    /// front at all and never will handshake on 443: PUBG's lobby <c>zk-ga-pcprod.acs.pubg.com</c> is
    /// an AWS Global Accelerator on TCP 40002, Steam's <c>p2p-*.discovery.steamserver.net</c> answer
    /// only their own protocol. Re-probing them cost the game 2.7 s per lookup on 2026-10-02, for an
    /// answer that is always the same - the upstream's, relayed. Short enough that a name whose edges
    /// were filtered only for a while gets its own addresses back within minutes.
    /// </summary>
    private static readonly TimeSpan NoEdgeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Enough candidates to find a good one, few enough that a cache miss stays quick. The probes
    /// run together, so the cost of a miss is one handshake's worth of time, not six.
    /// </summary>
    private const int MaxCandidates = 6;

    /// <summary>
    /// How long a name's own addresses get before borrowed edges join the race. A working edge
    /// answers in about 90 ms, so a name that is fine never borrows; a filtered one hangs for the
    /// whole budget, so waiting longer than this only delays the rescue.
    /// </summary>
    private static readonly TimeSpan HeadStart = TimeSpan.FromMilliseconds(300);

    /// <summary>After the first working edge, how long to wait for others that are about to finish.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(100);

    private readonly DohUpstream _doh;
    private readonly Action<string> _log;

    private readonly ConcurrentDictionary<string, Entry> _known;

    /// <summary>
    /// How long a verdict may still be ANSWERED with after it stops being fresh (<see cref="Lifetime"/>,
    /// <see cref="NoEdgeLifetime"/>) - while a new probe runs in the background for the next lookup. Without it every
    /// lookup after two minutes waited for handshakes again, and a game asking its names at start-up waited for all
    /// of them (FPT, 2026-10-02: "Initializing..." seconds longer). An edge that went bad in the meantime is answered
    /// at most once more; the probe already running replaces it.
    /// </summary>
    private static readonly TimeSpan UsableFor = TimeSpan.FromMinutes(30);

    /// <summary>Cancelled when the resolver stops; the background probes run under it.</summary>
    private readonly CancellationToken _background;

    /// <summary>
    /// The verdicts, kept by the caller so they outlive this instance - see <see cref="EdgeMemory"/>. A fresh one
    /// when the caller keeps nothing.
    /// </summary>
    internal sealed class Memory
    {
        internal ConcurrentDictionary<string, Entry> Known { get; } = new();
        internal ConcurrentDictionary<IPAddress, DateTimeOffset> Pool { get; } = new();
        internal ConcurrentDictionary<string, DateTimeOffset> Cut { get; } = new();
    }

    /// <summary>
    /// Every address recently seen to complete a handshake for ANY claimed name.
    ///
    /// It exists because of what was measured on 2026-09-22: 1.1.1.1 returned exactly one address
    /// for store.steampowered.com and it was a filtered edge, so probing the name's own candidates
    /// found nothing and the resolver fell back to relaying the bad answer. Meanwhile the two edges
    /// just proven good for steamcommunity.com served the store with a VALID certificate for it -
    /// they are the same Akamai property, and an edge that is clean for one of these names is clean
    /// for the others.
    ///
    /// Borrowed addresses are still probed for the name they are about to answer, so this is a
    /// shortlist of things worth trying, never a claim that they will work.
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> _pool;

    /// <summary>
    /// The probe in flight for a name, so a browser opening eight connections at once causes one
    /// round of handshakes and not eight. Removed as soon as it finishes.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<IPAddress[]>> _inFlight = new();

    /// <summary>
    /// Names the line was just seen cutting by name, until when: every own address took the connection, nothing
    /// completed, and another name completed on the same address. Read by the resolver to send the name through
    /// the tunnel on its own - see <see cref="CutByName"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cut;

    /// <summary>For the confirming handshakes: as long as a filter takes to reset, not as long as it can hang.</summary>
    private static readonly TimeSpan CutConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CutHandshakeTimeout = TimeSpan.FromMilliseconds(1500);

    private long _probed;
    private long _rejected;

    /// <summary>
    /// Called with every address about to be probed for a name, before the probe. The tunnel's instance routes them
    /// through the tunnel here, so the probe - and then the program - goes the way the answer will be used.
    /// </summary>
    private readonly Action<string, IReadOnlyList<IPAddress>>? _beforeProbe;

    /// <summary>
    /// Told the name when its own addresses took the connection and still nothing completed a handshake: the
    /// line is cutting it by name. Feeds the unblock report (UnblockReporter, trigger "filtered").
    /// </summary>
    private readonly Action<string>? _onFiltered;

    /// <summary>
    /// True for a name the tunnel will carry if the line turns out to cut it: then no edge is borrowed for it - see the
    /// borrowing in <see cref="ProbeAsync"/>. Asked per probe, because the tunnel comes and goes.
    /// </summary>
    private readonly Func<string, bool>? _tunnelFirst;

    /// <summary>Whether a name cut on every edge it was given is asked for as other Vietnamese networks - the line's own instance only.</summary>
    private readonly bool _inCountry;

    /// <summary>
    /// The tunnel's instance: of the edges that work, those named for the player's own network win - see
    /// EdgeRanking.PreferPlayersNetwork. Not the line's own: there the handshake is timed from the player, and the
    /// fastest is the right one.
    /// </summary>
    private readonly bool _preferPlayersNetwork;

    /// <summary>
    /// Set on the tunnel's instance: how fresh a verdict for a name <see cref="_keepFreshFor"/> accepts is kept, by
    /// probing it again in the background before it ages, whether or not anything asks. Null keeps the plain
    /// <see cref="Lifetime"/> and probes only when asked.
    ///
    /// Why: PUBG asks prod-live-front.playbattlegrounds.com.cn only when a match ends, so the verdict it got was as old
    /// as the match, answered stale (see <see cref="UsableFor"/>), and Tencent's mainland edges come and go within
    /// minutes. Viettel, 2026-10-04: handed a 4-minute-old edge that no longer answered, the game sat on SynSent for
    /// 12 s before trying the next address - the whole of a 10-15 s wait after every match.
    /// </summary>
    private readonly TimeSpan? _keepFresh;

    /// <summary>Which names are kept fresh - the profile's tunnel list, read per pass because the policy can change.</summary>
    private readonly Func<string, bool>? _keepFreshFor;

    /// <summary>When a client last asked for each name: a name nobody asked for in <see cref="KeepFreshAfterAsked"/> is let age.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAsked = new();

    /// <summary>Longer than a match, so the name the game asks only when a match ends is still being kept fresh then.</summary>
    private static readonly TimeSpan KeepFreshAfterAsked = TimeSpan.FromMinutes(60);

    private readonly TimeSpan _lifetime;

    /// <summary>The TTL for an answer from this instance: a verdict kept fresh is no use if Windows caches it longer.</summary>
    public uint AnswerTtl => _keepFresh is { } fresh ? (uint)fresh.TotalSeconds : AnswerTtlSeconds;

    public WorkingEdges(DohUpstream doh, Action<string> log, Action<string, IReadOnlyList<IPAddress>>? beforeProbe = null,
        Action<string>? onFiltered = null, Func<string, bool>? tunnelFirst = null, bool inCountry = false,
        bool preferPlayersNetwork = false, Memory? memory = null, CancellationToken background = default,
        TimeSpan? keepFresh = null, Func<string, bool>? keepFreshFor = null)
    {
        memory ??= new Memory();
        _known = memory.Known;
        _pool = memory.Pool;
        _cut = memory.Cut;
        _background = background;
        _inCountry = inCountry;
        _preferPlayersNetwork = preferPlayersNetwork;
        _doh = doh;
        _log = log;
        _beforeProbe = beforeProbe;
        _onFiltered = onFiltered;
        _tunnelFirst = tunnelFirst;
        _keepFresh = keepFreshFor is null ? null : keepFresh;
        _keepFreshFor = keepFreshFor;
        _lifetime = _keepFresh ?? Lifetime;
        if (_keepFresh is { } every && background.CanBeCanceled) _ = Task.Run(() => KeepFreshAsync(every, background));
    }

    /// <summary>
    /// Every third of <paramref name="every"/>: each listed name a client asked for within the hour, whose verdict is
    /// about to age, is probed again quietly - so the next answer is never much older than <paramref name="every"/>.
    /// Says so only when the edge handed out first changes, which is the one the game would have tried.
    /// </summary>
    private async Task KeepFreshAsync(TimeSpan every, CancellationToken ct)
    {
        var tick = every / 3;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(tick, ct).ConfigureAwait(false);
                KeepFreshPass(tick, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One pass of <see cref="KeepFreshAsync"/>; returns how many names it started probing. Internal for TunnelCheck.</summary>
    internal int KeepFreshPass(TimeSpan tick, CancellationToken ct)
    {
        var started = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, entry) in _known)
        {
            if (entry.Addresses.Length == 0 || entry.Expires - now > tick) continue;
            if (!_lastAsked.TryGetValue(name, out var asked) || now - asked > KeepFreshAfterAsked) continue;
            if (_keepFreshFor?.Invoke(name) != true || _inFlight.ContainsKey(name)) continue;

            var was = entry.Addresses;
            var task = _inFlight.GetOrAdd(name, key => Task.Run(() => ProbeAsync(key, null, ct, quiet: true)));
            started++;
            _ = task.ContinueWith(t =>
            {
                _inFlight.TryRemove(new KeyValuePair<string, Task<IPAddress[]>>(name, task));
                if (t.Status != TaskStatus.RanToCompletion) { _ = t.Exception; return; }
                if (t.Result.Length > 0 && !t.Result[0].Equals(was[0]))
                {
                    _log($"Unblock: kept {name} fresh - {was[0]} no longer leads, now {string.Join(", ", t.Result.Select(a => a.ToString()))}.");
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return started;
    }

    public long Probed => Interlocked.Read(ref _probed);

    /// <summary>How many addresses were dropped for failing a handshake - the reason this class exists.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    internal sealed record Entry(IPAddress[] Addresses, DateTimeOffset Expires, DateTimeOffset UsableUntil);

    /// <summary>
    /// True when the last probe of <paramref name="name"/> found the line cutting it by name and nothing on the line
    /// rescued it - so an honest address will not connect, and only the tunnel can help. Lasts as long as that probe's
    /// verdict (<see cref="NoEdgeLifetime"/>).
    /// </summary>
    public bool CutByName(string name) => _cut.TryGetValue(name, out var until) && until > DateTimeOffset.UtcNow;

    /// <summary>
    /// The addresses to answer with, or null when none could be found and the caller should fall
    /// back to relaying the upstream's own reply.
    /// </summary>
    /// <param name="sibling">
    /// A name of the same service known to work - its canary - or null when this IS that name.
    /// Used only when every address for <paramref name="name"/> turns out to be filtered.
    /// </param>
    public async Task<IPAddress[]?> ForAsync(string name, string? sibling, CancellationToken ct)
    {
        if (_keepFresh is not null) _lastAsked[name] = DateTimeOffset.UtcNow;
        if (_known.TryGetValue(name, out var entry))
        {
            var now = DateTimeOffset.UtcNow;

            // Empty is a remembered "nothing completed a handshake": relay, do not probe again.
            if (entry.Expires > now) return entry.Addresses.Length == 0 ? null : entry.Addresses;

            // Stale but still usable: answered now, probed again in the background for the next lookup - the game
            // never waits for handshakes it waited for once already. See UsableFor.
            if (entry.UsableUntil > now)
            {
                RefreshInBackground(name, sibling);
                return entry.Addresses.Length == 0 ? null : entry.Addresses;
            }
        }

        var task = _inFlight.GetOrAdd(name, key => ProbeAsync(key, sibling, ct));

        try
        {
            var addresses = await task.ConfigureAwait(false);
            return addresses.Length == 0 ? null : addresses;
        }
        finally
        {
            _inFlight.TryRemove(name, out _);
        }
    }

    /// <summary>A new probe for a stale verdict, nobody waiting on it. One per name at a time, like any probe.</summary>
    private void RefreshInBackground(string name, string? sibling)
    {
        if (_background.IsCancellationRequested || _inFlight.ContainsKey(name)) return;
        var task = _inFlight.GetOrAdd(name, key => Task.Run(() => ProbeAsync(key, sibling, _background)));
        _ = task.ContinueWith(t =>
        {
            _ = t.Exception;   // observed: a refresh cut short by the resolver stopping is not an error
            _inFlight.TryRemove(new KeyValuePair<string, Task<IPAddress[]>>(name, task));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<IPAddress[]> ProbeAsync(string name, string? sibling, CancellationToken ct, bool quiet = false)
    {
        var clock = Stopwatch.StartNew();
        Action<string> say = quiet ? _ => { } : _log;

        // Every upstream, not the first one that answers. The whole failure this fixes was one
        // resolver naming a filtered edge while another named a clean one, so asking only the
        // preferred resolver would reproduce it exactly.
        var perUpstream = new List<IReadOnlyList<IPAddress>>();
        var playersNetwork = new HashSet<IPAddress>();

        foreach (var (resolver, reply) in await _doh.ResolveEachAsync(
                     DnsWire.BuildQuery(0, name), ct).ConfigureAwait(false))
        {
            DnsMessage parsed;
            try { parsed = DnsWire.Parse(reply, reply.Length); }
            catch (FormatException) { continue; }

            List<IPAddress> addresses = [.. parsed.Addresses
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)];
            perUpstream.Add(addresses);
            if (DohUpstream.AnswersForPlayersNetwork(resolver)) playersNetwork.UnionWith(addresses);
        }

        // Taken in turn from each upstream, not one list after the other: they disagree about which
        // CDN to send this line to, and the one that knows the player's subnet is usually right.
        // Cloudflare named thirteen Tencent addresses for PUBG's lobby and Google one Akamai, and
        // the first six alone never reached Akamai - see EdgeRanking.
        var tried = EdgeRanking.Interleave(perUpstream, MaxCandidates);

        if (tried.Length == 0)
        {
            say($"Unblock: no encrypted resolver returned an address for {name}.");
            return [];
        }

        // One round for everything, cancelled the moment an answer is settled. A filtered edge does
        // not fail, it hangs for the whole budget, and waiting for every probe to report meant one
        // hanging address cost 2.5 s even when a good one had answered in 90 ms. Measured
        // 2026-09-24: the overlay's first store lookup took 5069 ms, two full budgets back to back,
        // for an edge that handshakes in under a tenth of a second.
        _beforeProbe?.Invoke(name, tried);

        using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var roundClock = Stopwatch.StartNew();
        var attempts = tried.Select(a => Attempt.Start(a, own: true, name, roundClock, round.Token)).ToList();

        var borrowed = Array.Empty<IPAddress>();

        // Give the name's own addresses a head start, then borrow in parallel rather than after.
        // Most names never get this far: their own edge answers inside the head start and nothing
        // is borrowed, so a normal lookup costs what it did before.
        //
        // Not when every own address has already refused the connection: borrowed edges could never
        // be used for this name (see below), and probing them only made Steam's p2p discovery names
        // wait out the full budget - 2.7 s for an answer known after 0.3.
        //
        // Nor when the tunnel can carry the name's own addresses. A borrowed edge proves it holds a
        // certificate for the name and answers HTTP for it, and that is still not serving it: on
        // 2026-10-02 FPT in Ho Chi Minh City cut prod-live-cfentry and prod-live-images, prod-live-
        // front's Akamai edge answered them 400, and once that was refused, prod-live-xenuine's AWS
        // ingress answered them 403 - passed, handed to PUBG, "cannot connect" again. The name's own
        // addresses through the tunnel are right by construction; a borrowed edge is a guess.
        var tunnelFirst = _tunnelFirst?.Invoke(name) == true;
        if (!tunnelFirst &&
            !await AnyWorksAsync(Tasks(attempts), HeadStart).ConfigureAwait(false) &&
            !attempts.All(a => a.Task.IsCompleted && !a.Connected))
        {
            borrowed = await ShortlistAsync(name, sibling, tried, ct).ConfigureAwait(false);
            if (borrowed.Length > 0) _beforeProbe?.Invoke(name, borrowed);
            attempts.AddRange(borrowed.Select(a => Attempt.Start(a, own: false, name, roundClock, round.Token)));
        }

        // Once something works, a short grace to collect whatever else is about to finish - two
        // good edges are worth more than one - then drop the rest.
        if (await AnyWorksAsync(Tasks(attempts), Timeout.InfiniteTimeSpan).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.WhenAll(Tasks(attempts)), Task.Delay(Grace)).ConfigureAwait(false);

            if (!attempts.Any(a => a.Own && a.Task.IsCompleted && a.Task.Result.Works))
            {
                await WaitForSlowOwnAsync(attempts, roundClock).ConfigureAwait(false);
            }
        }

        // Only what finished before the cancel is a verdict. A probe cut off here reports false,
        // but it was abandoned, not rejected, and counting it would blame edges that did nothing.
        var settled = attempts.Where(a => a.Task.IsCompleted).ToArray();
        round.Cancel();
        await Task.WhenAll(Tasks(attempts)).ConfigureAwait(false);

        var working = settled.Where(a => a.Task.Result.Works).ToArray();
        var bad = settled.Length - working.Length;
        var abandoned = attempts.Count - settled.Length;

        // The name's own addresses whenever one of them works. A borrowed edge only proved that it
        // holds a certificate for this name and does not refuse it over HTTP, and a wildcard proves
        // the first for a different service: on 2026-10-02 *.acs.pubg.com let acrt-pcprod's servers
        // pass for zk-ga-pcprod, PUBG's lobby on TCP 40002, and prod-live-front's Akamai edge for the
        // xenuine API - and later the same evening for prod-live-cfentry, answering it 400.
        //
        // And borrowed edges only for a name that IS a web front on this line: one of its own
        // addresses took the TCP connection and then failed the handshake - a reset, a stall, a
        // forged certificate, the shape of the Steam filtering this exists for. A name none of
        // whose own addresses even accepts a connection on 443 is not served over HTTPS here (the
        // lobby's Global Accelerator, Steam's p2p discovery), and any edge borrowed for it is wrong.
        var ownConnected = attempts.Any(a => a.Own && a.Connected);
        var ownWorking = working.Where(a => a.Own).ToArray();

        // Cut on every address this line was given - but those are only the edges the CDN picked for
        // this subnet. Asked as other Vietnamese networks, it names edges inside the country, and the
        // line's filter, which sits on its international links, never sees them. They are the name's
        // own answers, so right by construction, and they are tried before any borrowed edge or the
        // tunnel. See InCountrySubnets.
        Attempt[] inCountry = [];
        if (ownWorking.Length == 0 && ownConnected && _inCountry)
        {
            inCountry = await InCountryAsync(name, tried, roundClock, ct).ConfigureAwait(false);
            if (inCountry.Length > 0)
            {
                ownWorking = inCountry;
                working = [.. working, .. inCountry];
            }
        }
        var usable = ownWorking.Length > 0 ? ownWorking
            : ownConnected ? working
            : [];

        // Through the tunnel the probe is timed from the relay, not from the player, so its speed cannot choose the
        // CDN: what was named for the player's own network is kept when any of it works. See
        // EdgeRanking.PreferPlayersNetwork - PUBG's lobby went to mainland China this way on FPT, 2026-10-02.
        if (_preferPlayersNetwork && usable.Length > 1)
        {
            var preferred = EdgeRanking.PreferPlayersNetwork(usable, a => a.Address, playersNetwork);
            if (preferred.Count < usable.Length)
            {
                say($"Unblock: {name} through the tunnel - kept {string.Join(", ", preferred.Select(a => a.Address))}, " +
                     $"named for this network, over {usable.Length - preferred.Count} edge(s) named for somewhere else.");
                usable = [.. preferred];
            }
        }

        // Fastest first, and a clearly slower CDN not handed out at all - see EdgeRanking.
        var good = EdgeRanking.Rank([.. usable.Select(a => (a.Address, a.Task.Result.Elapsed))]);
        var slower = usable.Length - good.Length;

        Interlocked.Add(ref _probed, settled.Length);
        Interlocked.Add(ref _rejected, bad);

        if (good.Length == 0)
        {
            // Nothing survived, borrowed edges included. Say so plainly rather than answering with
            // addresses known to hang: the caller relays the upstream's reply instead, which is no
            // worse than before this class existed, and the log carries the fact that this line
            // filters every edge for this name - which would mean the tunnel, not DNS.
            // The pool size is in the message because without it this line has two very different
            // meanings that look identical: "every edge of this CDN is filtered here", which needs
            // a tunnel, and "the shortlist of known-good edges happened to be empty", which is a
            // timing accident and fixes itself. Guessing between them from a player's log cost an
            // evening once already.
            var why = working.Length > 0
                ? $"{working.Length} borrowed edge(s) passed, but none of its own addresses accepted a connection on 443, so it is not a web front here"
                : tunnelFirst
                    ? $"{tried.Length} from the resolvers, none borrowed - the tunnel can carry its own"
                    : $"{tried.Length} from the resolvers, {borrowed.Length} borrowed, {_pool.Count} in the pool";
            say($"Unblock: no address for {name} completed a handshake ({clock.ElapsedMilliseconds} ms) - " +
                 $"{why}. Relaying the upstream answer unchanged for {NoEdgeLifetime.TotalMinutes:0} min.");
            var until = DateTimeOffset.UtcNow.Add(NoEdgeLifetime);
            _known[name] = new Entry([], until, DateTimeOffset.UtcNow.Add(UsableFor));

            // "Its own addresses took the connection" is not yet "the line cuts this name": Steam's
            // p2p-*.discovery names accept on 443 and speak no TLS at all. Only the comparison says which -
            // the same address, another name - and it is what decides whether the tunnel may take it.
            if (ownConnected &&
                await IsCutAsync(name, [.. attempts.Where(a => a.Own && a.Connected).Select(a => a.Address)], ct)
                    .ConfigureAwait(false))
            {
                _cut[name] = until;
                _onFiltered?.Invoke(name);
            }
            return [];
        }

        if (!good.Any(tried.Contains) && inCountry.Length == 0)
        {
            say($"Unblock: no address the upstreams gave for {name} worked in time on this line; " +
                 $"answering with {good.Length} edge(s) proven for another name of the same service " +
                 $"({clock.ElapsedMilliseconds} ms).");
        }

        if (bad > 0 || abandoned > 0 || slower > 0)
        {
            say($"Unblock: {name} -> {string.Join(", ", good.Select(a => a.ToString()))} " +
                 $"({bad} address(es) dropped for failing a TLS handshake, {abandoned} still pending " +
                 $"and abandoned, {slower} working but clearly slower than the fastest " +
                 $"({usable.Min(a => a.Task.Result.Elapsed).TotalMilliseconds:0} ms handshake), {clock.ElapsedMilliseconds} ms).");
        }

        // Every working edge goes in the pool, slower ones included: the pool is a shortlist for
        // other names to probe, and they are ranked again for the name that borrows them.
        var expires = DateTimeOffset.UtcNow.Add(_lifetime);
        foreach (var a in working) _pool[a.Address] = expires;

        _known[name] = new Entry(good, expires, DateTimeOffset.UtcNow.Add(UsableFor));
        return good;
    }

    /// <summary>
    /// Networks inside Vietnam to ask the name's DNS for it as, when every edge this line was given is cut.
    ///
    /// Measured 2026-10-02 for prod-live-images (CloudFront), through Google with Client Subnet: 27.64.0.0/24 and
    /// 123.20.0.0/24 were sent to SGN50 in Ho Chi Minh City, 118.69.0.0/24 and 14.160.0.0/24 to HAN51 in Ha Noi -
    /// while FPT's own 42.116.116.0/24 was sent to Hong Kong and Singapore, every edge of which FPT cuts by name. The
    /// HCM player whose images crawled through the relay at 256 KB/s fetched them from HAN51 directly: 200 in
    /// 0.25 s. Two cities, two operators each, so one network renumbered does not lose a city.
    /// </summary>
    private static readonly (IPAddress Network, int Prefix)[] InCountrySubnets =
    [
        (IPAddress.Parse("27.64.0.0"), 24), (IPAddress.Parse("123.20.0.0"), 24),
        (IPAddress.Parse("118.69.0.0"), 24), (IPAddress.Parse("14.160.0.0"), 24),
    ];

    /// <summary>
    /// The name's edges as <see cref="InCountrySubnets"/> are told them, minus those already tried, probed on this
    /// line as its own addresses. Returns the ones that completed a handshake; empty when the CDN does not map by
    /// subnet (the same addresses come back) or none of them gets through either.
    /// </summary>
    private async Task<Attempt[]> InCountryAsync(string name, IPAddress[] tried, Stopwatch roundClock, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var perSubnet = new List<IReadOnlyList<IPAddress>>();
        foreach (var reply in await _doh.ResolveAsSubnetsAsync(name, InCountrySubnets, ct).ConfigureAwait(false))
        {
            try
            {
                perSubnet.Add([.. DnsWire.Parse(reply, reply.Length).Addresses
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !tried.Contains(a))]);
            }
            catch (FormatException) { }
        }

        var candidates = EdgeRanking.Interleave(perSubnet, MaxCandidates);
        if (candidates.Length == 0) return [];

        using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = candidates.Select(a => Attempt.Start(a, own: true, name, roundClock, round.Token)).ToList();
        if (await AnyWorksAsync(Tasks(attempts), Timeout.InfiniteTimeSpan).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.WhenAll(Tasks(attempts)), Task.Delay(Grace)).ConfigureAwait(false);
        }
        var found = attempts.Where(a => a.Task.IsCompleted && a.Task.Result.Works).ToArray();
        round.Cancel();
        await Task.WhenAll(Tasks(attempts)).ConfigureAwait(false);

        _log(found.Length > 0
            ? $"Unblock: every edge this line was given for {name} is cut, but as other Vietnamese networks see it, it is " +
              $"also served from {string.Join(", ", found.Select(a => a.Address))} - and those work here " +
              $"({clock.ElapsedMilliseconds} ms)."
            : $"Unblock: {name} asked as other Vietnamese networks: {string.Join(", ", candidates.Select(a => a.ToString()))}, " +
              $"none of which works on this line either ({clock.ElapsedMilliseconds} ms).");
        return found;
    }

    /// <summary>
    /// Whether the line cuts handshakes naming <paramref name="name"/>: on up to two of its own addresses, the real
    /// name and the control name side by side, and any address where the real one is reset or stalls while the
    /// control gets an answer. What tools\Check-Unblock.ps1 and UnblockDiagnosis compare, asked in a fraction of
    /// their time - an FPT reset lands within 25 ms.
    /// </summary>
    private async Task<bool> IsCutAsync(string name, IPAddress[] connected, CancellationToken ct)
    {
        var checks = connected.Take(2).Select(async address =>
        {
            var real = EdgeProber.HandshakeAsync(address, name, CutConnectTimeout, CutHandshakeTimeout, ct);
            var control = EdgeProber.HandshakeAsync(address, EdgeProber.ControlName, CutConnectTimeout, CutHandshakeTimeout, ct);
            var (realOutcome, _) = await real.ConfigureAwait(false);
            var (controlOutcome, _) = await control.ConfigureAwait(false);
            return (Address: address, Real: realOutcome, Control: controlOutcome);
        });

        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        var cut = results.FirstOrDefault(r => EdgeProber.CutByName(r.Real, r.Control));
        if (cut.Address is not null)
        {
            _log($"Unblock: the line cuts {name} by name - on {cut.Address} its handshake is {cut.Real}, " +
                 $"{EdgeProber.ControlName}'s is {cut.Control}.");
            return true;
        }

        _log($"Unblock: {name} is not cut by name here - " +
             string.Join(", ", results.Select(r => $"{r.Address} real {r.Real}, control {r.Control}")) +
             ". Not a web front for this name, so nothing to send through the tunnel.");
        return false;
    }

    /// <summary>
    /// Edges proven for other names, for when the name's own addresses are not answering: the
    /// sibling's first, then the rest of the pool, most recently proven first.
    /// </summary>
    private async Task<IPAddress[]> ShortlistAsync(
        string name, string? sibling, IPAddress[] tried, CancellationToken ct)
    {
        var shortlist = new List<IPAddress>();

        // The canary's edges FIRST, always - not only when the pool is empty. The pool holds
        // every address proven for ANY claimed name, and most of those are not the CDN at all:
        // api/login/chat/valvesoftware are Valve's own servers and fail this name's certificate.
        // On 2026-09-24 the Steam overlay asked for those names before the store, the pool held
        // 7 addresses, the cap took 6 of them in dictionary order, and the one Akamai edge that
        // works (just proven for steamcommunity.com) was the one left out. The store fell back
        // to the filtered answer inside the game while the Steam client, which asks for the
        // store first, was fine.
        //
        // The canary is the one name this service has already proven answers correctly on this
        // line. When its verdict is fresh this costs nothing; when not, one DoH round trip plus
        // one handshake.
        if (sibling is not null && sibling != name)
        {
            if (!(_known.TryGetValue(sibling, out var known) && known.Expires > DateTimeOffset.UtcNow))
            {
                _log($"Unblock: no address for {name} answered within {HeadStart.TotalMilliseconds:0} ms " +
                     $"and {sibling} has no fresh verdict - asking it for a usable edge.");
            }

            // Null sibling on the way in: this must not be able to recurse.
            var fromSibling = await ForAsync(sibling, null, ct).ConfigureAwait(false);
            if (fromSibling is not null) shortlist.AddRange(fromSibling);
        }

        shortlist.AddRange(_pool
            .Where(p => p.Value > DateTimeOffset.UtcNow)
            .OrderByDescending(p => p.Value)
            .Select(p => p.Key));

        return shortlist
            .Distinct()
            .Where(a => !tried.Contains(a))
            .Take(MaxCandidates)
            .ToArray();
    }

    /// <summary>One address being probed for one name, and whether its TCP connection was taken.</summary>
    private sealed class Attempt
    {
        private long _connectedAtMs = -1;

        private Attempt(IPAddress address, bool own)
        {
            Address = address;
            Own = own;
        }

        public IPAddress Address { get; }

        /// <summary>One of the name's own addresses, as opposed to an edge borrowed from another name.</summary>
        public bool Own { get; }

        public Task<(bool Works, TimeSpan Elapsed)> Task { get; private set; } = null!;

        public bool Connected => Interlocked.Read(ref _connectedAtMs) >= 0;

        /// <summary>When the TCP connection was taken, on the round's clock, or -1.</summary>
        public long ConnectedAtMs => Interlocked.Read(ref _connectedAtMs);

        public static Attempt Start(IPAddress address, bool own, string name, Stopwatch round, CancellationToken ct)
        {
            var attempt = new Attempt(address, own);
            attempt.Task = attempt.RunAsync(name, round, ct);
            return attempt;
        }

        // Elapsed is the probe's own, from its start - borrowed edges start after the head start.
        // A borrowed edge must also answer as a host of the name, not just hold its certificate - see
        // EdgeProber.ServesNameAsync. The name's own addresses are not asked: it costs a round trip, and an
        // upstream naming them is already the claim that they serve it.
        private async Task<(bool Works, TimeSpan Elapsed)> RunAsync(string name, Stopwatch round, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            var works = await EdgeProber.WorksAsync(Address, name, ct,
                () => Interlocked.Exchange(ref _connectedAtMs, round.ElapsedMilliseconds),
                servesName: !Own).ConfigureAwait(false);
            return (works, clock.Elapsed);
        }
    }

    private static List<Task<(bool Works, TimeSpan Elapsed)>> Tasks(IEnumerable<Attempt> attempts) =>
        [.. attempts.Select(a => a.Task)];

    /// <summary>
    /// A borrowed edge answered first. Before trusting it, an own address that has its connection and
    /// is still mid-handshake gets the time an honest handshake over that distance needs - four times
    /// its connect time, plus a little. acrt-pcprod.acs.pubg.com is in us-east: connected at 240 ms,
    /// handshake done at 711, well after the head start. A filtered edge is the opposite shape: it
    /// connects in 30 ms and then hangs, so this waits about a third of a second, not the 19 s it
    /// takes to send its reset.
    /// </summary>
    private static async Task WaitForSlowOwnAsync(List<Attempt> attempts, Stopwatch round)
    {
        var midHandshake = attempts.Where(a => a.Own && a.Connected && !a.Task.IsCompleted).ToList();
        if (midHandshake.Count == 0) return;

        var deadline = midHandshake.Max(a => a.ConnectedAtMs * 4 + 200);
        var wait = deadline - round.ElapsedMilliseconds;
        if (wait <= 0) return;

        await AnyWorksAsync(Tasks(midHandshake), TimeSpan.FromMilliseconds(wait)).ConfigureAwait(false);
    }

    /// <summary>
    /// True as soon as any probe reports a working edge; false once all have failed or the wait
    /// is over. Never throws and never cancels anything.
    /// </summary>
    private static async Task<bool> AnyWorksAsync(
        IReadOnlyList<Task<(bool Works, TimeSpan Elapsed)>> probes, TimeSpan wait)
    {
        var deadline = wait == Timeout.InfiniteTimeSpan ? null : Task.Delay(wait);
        var pending = probes.ToList();

        while (true)
        {
            // Snapshot first: a probe finishing between the check and the removal must not be
            // dropped unread.
            var done = pending.Where(p => p.IsCompleted).ToList();
            if (done.Any(p => p.Result.Works)) return true;
            pending.RemoveAll(done.Contains);
            if (pending.Count == 0) return false;

            var next = Task.WhenAny(pending);
            if (deadline is not null && await Task.WhenAny(next, deadline).ConfigureAwait(false) == deadline)
            {
                return false;
            }

            await next.ConfigureAwait(false);
        }
    }
}
