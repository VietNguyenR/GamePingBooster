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

    private readonly ConcurrentDictionary<string, Entry> _known = new();

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
    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> _pool = new();

    /// <summary>
    /// The probe in flight for a name, so a browser opening eight connections at once causes one
    /// round of handshakes and not eight. Removed as soon as it finishes.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<IPAddress[]>> _inFlight = new();

    private long _probed;
    private long _rejected;

    public WorkingEdges(DohUpstream doh, Action<string> log)
    {
        _doh = doh;
        _log = log;
    }

    public long Probed => Interlocked.Read(ref _probed);

    /// <summary>How many addresses were dropped for failing a handshake - the reason this class exists.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    private sealed record Entry(IPAddress[] Addresses, DateTimeOffset Expires);

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
        if (_known.TryGetValue(name, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        {
            return entry.Addresses;
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

    private async Task<IPAddress[]> ProbeAsync(string name, string? sibling, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        // Every upstream, not the first one that answers. The whole failure this fixes was one
        // resolver naming a filtered edge while another named a clean one, so asking only the
        // preferred resolver would reproduce it exactly.
        var candidates = new List<IPAddress>();

        foreach (var reply in await _doh.ResolveEverywhereAsync(
                     DnsWire.BuildQuery(0, name), ct).ConfigureAwait(false))
        {
            DnsMessage parsed;
            try { parsed = DnsWire.Parse(reply, reply.Length); }
            catch (FormatException) { continue; }

            foreach (var address in parsed.Addresses)
            {
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (!candidates.Contains(address)) candidates.Add(address);
            }
        }

        if (candidates.Count == 0)
        {
            _log($"Unblock: no encrypted resolver returned an address for {name}.");
            return [];
        }

        var tried = candidates.Take(MaxCandidates).ToArray();

        // One round for everything, cancelled the moment an answer is settled. A filtered edge does
        // not fail, it hangs for the whole budget, and waiting for every probe to report meant one
        // hanging address cost 2.5 s even when a good one had answered in 90 ms. Measured
        // 2026-09-24: the overlay's first store lookup took 5069 ms, two full budgets back to back,
        // for an edge that handshakes in under a tenth of a second.
        using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probes = tried.Select(a => ProbeOneAsync(a, name, round.Token)).ToList();

        var borrowed = Array.Empty<IPAddress>();

        // Give the name's own addresses a head start, then borrow in parallel rather than after.
        // Most names never get this far: their own edge answers inside the head start and nothing
        // is borrowed, so a normal lookup costs what it did before.
        if (!await AnyWorksAsync(probes, HeadStart).ConfigureAwait(false))
        {
            borrowed = await ShortlistAsync(name, sibling, tried, ct).ConfigureAwait(false);
            probes.AddRange(borrowed.Select(a => ProbeOneAsync(a, name, round.Token)));
        }

        // Once something works, a short grace to collect whatever else is about to finish - two
        // good edges are worth more than one - then drop the rest.
        if (await AnyWorksAsync(probes, Timeout.InfiniteTimeSpan).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.WhenAll(probes), Task.Delay(Grace)).ConfigureAwait(false);
        }

        // Only what finished before the cancel is a verdict. A probe cut off here reports false,
        // but it was abandoned, not rejected, and counting it would blame edges that did nothing.
        var settled = probes.Where(p => p.IsCompleted).Select(p => p.Result).ToArray();
        round.Cancel();
        await Task.WhenAll(probes).ConfigureAwait(false);

        var good = settled.Where(r => r.Works).Select(r => r.Address).ToArray();
        var bad = settled.Length - good.Length;
        var abandoned = probes.Count - settled.Length;

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
            _log($"Unblock: no address for {name} completed a handshake ({clock.ElapsedMilliseconds} ms) - " +
                 $"{tried.Length} from the resolvers, {borrowed.Length} borrowed, {_pool.Count} in the pool. " +
                 "Relaying the upstream answer unchanged.");
            return [];
        }

        if (!good.Any(tried.Contains))
        {
            _log($"Unblock: no address the upstreams gave for {name} worked in time on this line; " +
                 $"answering with {good.Length} edge(s) proven for another name of the same service " +
                 $"({clock.ElapsedMilliseconds} ms).");
        }

        if (bad > 0 || abandoned > 0)
        {
            _log($"Unblock: {name} -> {string.Join(", ", good.Select(a => a.ToString()))} " +
                 $"({bad} address(es) dropped for failing a TLS handshake, {abandoned} still pending " +
                 $"and abandoned, {clock.ElapsedMilliseconds} ms).");
        }

        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        foreach (var address in good) _pool[address] = expires;

        _known[name] = new Entry(good, expires);
        return good;
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

    private static async Task<(IPAddress Address, bool Works)> ProbeOneAsync(
        IPAddress address, string name, CancellationToken ct) =>
        (address, await EdgeProber.WorksAsync(address, name, ct).ConfigureAwait(false));

    /// <summary>
    /// True as soon as any probe reports a working edge; false once all have failed or the wait
    /// is over. Never throws and never cancels anything.
    /// </summary>
    private static async Task<bool> AnyWorksAsync(
        IReadOnlyList<Task<(IPAddress Address, bool Works)>> probes, TimeSpan wait)
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
