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
        var (good, bad) = await ProbeSetAsync(tried, name, ct).ConfigureAwait(false);
        var borrowedCount = 0;

        if (good.Length == 0)
        {
            // The name's own answer is entirely filtered. Before giving up, try the edges that
            // recently worked for a sibling name - measured to serve these sites with a valid
            // certificate for each - and probe them for THIS name so nothing is assumed.
            var shortlist = _pool
                .Where(p => p.Value > DateTimeOffset.UtcNow)
                .Select(p => p.Key)
                .ToList();

            // Nothing in the pool. Rather than give up - which is what happened on a real machine
            // on 2026-09-22, where the store fell back to a filtered address while a perfectly good
            // edge was one query away - go and find one: resolve a name of the same service that is
            // known to work, which fills the pool as a side effect.
            //
            // The canary, specifically. It is the one name this service has already proven answers
            // correctly on this line, and it costs one DoH round trip plus one handshake.
            if (shortlist.Count == 0 && sibling is not null && sibling != name)
            {
                _log($"Unblock: every address for {name} is filtered and nothing is known to work yet - " +
                     $"asking {sibling} for a usable edge.");

                // Null sibling on the way in: this must not be able to recurse.
                var fromSibling = await ForAsync(sibling, null, ct).ConfigureAwait(false);
                if (fromSibling is not null) shortlist.AddRange(fromSibling);
            }

            var borrowed = shortlist
                .Distinct()
                .Where(a => !tried.Contains(a))
                .Take(MaxCandidates)
                .ToArray();

            borrowedCount = borrowed.Length;

            if (borrowed.Length > 0)
            {
                var (rescued, alsoBad) = await ProbeSetAsync(borrowed, name, ct).ConfigureAwait(false);
                bad += alsoBad;

                if (rescued.Length > 0)
                {
                    _log($"Unblock: every address the upstreams gave for {name} is filtered on this " +
                         $"line; answering with {rescued.Length} edge(s) proven for another name of the same service " +
                         $"({clock.ElapsedMilliseconds} ms).");
                    good = rescued;
                }
            }
        }

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
                 $"{tried.Length} from the resolvers, {borrowedCount} borrowed, {_pool.Count} in the pool. " +
                 "Relaying the upstream answer unchanged.");
            return [];
        }

        if (bad > 0)
        {
            _log($"Unblock: {name} -> {string.Join(", ", good.Select(a => a.ToString()))} " +
                 $"({bad} address(es) dropped for failing a TLS handshake, {clock.ElapsedMilliseconds} ms).");
        }

        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        foreach (var address in good) _pool[address] = expires;

        _known[name] = new Entry(good, expires);
        return good;
    }

    /// <summary>Handshakes against every address at once and splits them into good and bad.</summary>
    private async Task<(IPAddress[] Good, int Bad)> ProbeSetAsync(
        IPAddress[] addresses, string name, CancellationToken ct)
    {
        var results = await Task.WhenAll(
            addresses.Select(async address => (Address: address,
                Works: await EdgeProber.WorksAsync(address, name, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);

        var good = results.Where(r => r.Works).Select(r => r.Address).ToArray();

        Interlocked.Add(ref _probed, addresses.Length);
        Interlocked.Add(ref _rejected, results.Length - good.Length);

        return (good, results.Length - good.Length);
    }
}
