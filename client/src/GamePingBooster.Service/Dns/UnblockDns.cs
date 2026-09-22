using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Answers the names this line lies about, and refuses to claim it worked when it did not.
///
/// The order matters and is the whole of the design:
///
///   1. read the resolvers Windows uses now, BEFORE anything is changed
///   2. ask each service's canary whether this line actually poisons it, so there is something to prove
///   3. start the local resolver and make sure it answers
///   4. only then point Windows at it
///   5. ask the canaries again through Windows - if no poisoned one changed, undo everything
///
/// Step 5 is there because the mechanism in step 4 cannot be tested from a developer's terminal:
/// writing NRPT rules needs SYSTEM, and a rule that Windows quietly ignores looks identical to a
/// rule that works. Rather than ship a guess, the service proves it on the machine it is running on
/// and rolls back if the proof fails. A feature that reports "could not enable" is recoverable; one
/// that silently does nothing while the player waits for a page to load is not.
///
/// WHICH services, and which names, comes from the profile the licence server delivers - see
/// <see cref="UnblockPolicy"/>. Nothing here knows about any particular one.
///
/// Nothing here touches the tunnel, the routes or the relay either. Unblocking a name is not a game
/// being accelerated, and the two halves of the product share only a switch.
/// </summary>
internal sealed class UnblockDns : IAsyncDisposable
{
    private readonly Func<UnblockPolicy> _policy;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LocalResolver? _resolver;
    private UnblockPolicy _active = UnblockPolicy.Empty;

    /// <summary>
    /// Whether this installation may unblock at all - the config switch, captured when Start ran.
    /// Without it <see cref="RefreshAsync"/> would quietly turn the feature on for somebody who
    /// switched it off, the first time a profile arrived.
    /// </summary>
    private bool _allowed;

    public UnblockDns(Func<UnblockPolicy> policy, Action<string> log)
    {
        _policy = policy;
        _log = log;
    }

    public bool Enabled { get; private set; }

    /// <summary>Why the last enable failed, or null. Shown in the status line, so it is user-facing prose.</summary>
    public string? LastError { get; private set; }

    /// <summary>The services currently unblocked, for the UI. Empty when the feature is off.</summary>
    public IReadOnlyList<string> Services =>
        Enabled ? [.. _active.Apps.Select(a => a.Name)] : [];

    /// <summary>One line for the status pipe, so the UI can say something true about this half of the product.</summary>
    public string Detail
    {
        get
        {
            if (!Enabled) return LastError ?? "off";

            var resolver = _resolver;
            return resolver is null
                ? "on"
                : $"on for {_active.Describe()} - {resolver.ScopedQueries} name(s) answered, " +
                  $"{resolver.ForwardedQueries} forwarded, {resolver.RejectedEdges} filtered edge(s) avoided";
        }
    }

    /// <summary>
    /// Removes anything a previous run left behind. Called at service start, before any decision.
    ///
    /// A rule pointing at a resolver that is not listening is worse than no rule: Windows will send
    /// the claimed names to a dead address and the service fails in a way that survives a reboot and
    /// has no visible cause. This process crashing must not be able to leave that behind, and the
    /// only place guaranteed to run afterwards is the next start.
    /// </summary>
    public static void RemoveLeftovers(Action<string> log)
    {
        var removed = NrptPolicy.Remove(log);
        if (removed > 0) log($"Removed {removed} unblock rule(s) left by a previous run.");
    }

    /// <summary>
    /// Keeps trying until it is on, for as long as the service runs.
    ///
    /// A loop and not a single attempt at startup, for two reasons that both bite on a real machine.
    /// The service starts with Windows and the network usually does not exist yet at that moment, so
    /// the first attempt on a cold boot fails for a reason that has nothing to do with this line.
    /// And the list of services comes from the profile, which is fetched after startup - so an early
    /// attempt can find nothing to do and has to look again once it has arrived.
    /// </summary>
    public void Start(CancellationToken ct)
    {
        _allowed = true;
        _ = Task.Run(async () => await RunAsync(ct).ConfigureAwait(false), ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);

        while (!ct.IsCancellationRequested && !Enabled)
        {
            try
            {
                if (await EnableAsync(ct).ConfigureAwait(false) is null) return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log($"Unblock: {ex.Message}");
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Backing off to a minute: the common failures - no network yet, no profile yet -
            // resolve themselves in seconds, and the uncommon one is a machine that will never
            // manage it, which must not write a log line every five seconds all day.
            delay = TimeSpan.FromMinutes(1);
        }
    }

    /// <summary>
    /// Re-applies the policy when the delivered profile has changed what it says.
    ///
    /// This exists because of what the first server-delivered build actually did on a real machine.
    /// The service starts, enables against whatever profile was on disk - seconds before the app has
    /// signed in and pushed a fresh one - and the enable loop then stops, because it is enabled. The
    /// licence server's list arrived two seconds later and was never read: the log said
    /// "Services: Steam (from built in)" on a machine whose profile carried a perfectly good list.
    ///
    /// So a profile arriving is an event this has to hear, not a thing to hope was already there.
    /// Called after every profile load and push - see PipeServer.
    ///
    /// A no-op when nothing changed, which is the common case: comparing the signature rather than
    /// re-applying blindly keeps a profile refresh from tearing the machine's DNS policy down and
    /// putting it back for no reason.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!_allowed) return;

        var wanted = _policy();

        // Signature, not reference equality: the policy is rebuilt from the profile on every read.
        if (Enabled && Signature(wanted) == Signature(_active)) return;

        if (Enabled)
        {
            _log($"Unblock: the profile now says {wanted.Describe()} (from {wanted.Source}) - re-applying.");
            await DisableAsync().ConfigureAwait(false);
        }

        var error = await EnableAsync(ct).ConfigureAwait(false);
        if (error is not null) _log($"Unblock: could not apply the new list - {error}");
    }

    /// <summary>Everything about a policy that changes what the machine does, as one string.</summary>
    private static string Signature(UnblockPolicy policy) =>
        string.Join("|", policy.Apps.Select(a =>
            $"{a.Id}:{string.Join(",", a.Scope)}:{string.Join(",", a.Excluded)}:{a.Canary}"));

    /// <summary>Returns null when it worked, or a sentence for the user when it did not.</summary>
    public async Task<string?> EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Enabled) return null;

            LastError = null;

            var policy = _policy();
            if (policy.IsEmpty)
            {
                // Not an error and not retried noisily: an account entitled to nothing, or a server
                // that has switched the feature off, is a decision rather than a fault.
                return Fail("Nothing to unblock - the profile names no services.");
            }

            // 1. The upstreams, before the policy can point them at us.
            var upstream = AdapterDns.Read();
            if (upstream.Count == 0)
            {
                return Fail("Windows has no resolver of its own to forward to. Unblocking stays off.");
            }

            // 2. Which canaries this line actually lies about. Asked before the resolver exists, so
            //    the answers are the line's own. A service that is not blocked here is not a failure
            //    - it just cannot take part in the proof, and says so rather than inventing one.
            var poisoned = new List<UnblockApp>();
            foreach (var app in policy.Apps)
            {
                if (LooksPoisoned(await ResolveThroughWindowsAsync(app.Canary).ConfigureAwait(false)))
                {
                    poisoned.Add(app);
                }
            }

            _log(poisoned.Count > 0
                ? $"This line poisons {string.Join(", ", poisoned.Select(a => a.Canary))} - " +
                  "the fix can be verified after it is applied."
                : $"This line answers every canary honestly ({policy.Describe()}) - nothing to verify " +
                  "against, enabling anyway.");

            // 3. The resolver first. Pointing Windows at an address nothing is listening on would
            //    take DNS down for the claimed names with no way back except a reboot.
            var resolver = new LocalResolver(upstream, policy, _log);
            try
            {
                resolver.Start();
            }
            catch (Exception ex)
            {
                await resolver.DisposeAsync().ConfigureAwait(false);
                return Fail($"Could not start the unblock resolver: {ex.Message}");
            }

            _resolver = resolver;
            _active = policy;

            if (!await AnswersItselfAsync(policy, ct).ConfigureAwait(false))
            {
                await RollBackAsync().ConfigureAwait(false);
                return Fail("The unblock resolver started but did not answer its own query.");
            }

            // 4. Now Windows can be pointed at it, one rule set per service so a later change to
            //    one does not disturb the others.
            try
            {
                var rules = 0;
                foreach (var app in policy.Apps)
                {
                    rules += NrptPolicy.Install(app.Id, app.Namespaces, LocalResolver.ListenAddress.ToString(), _log);
                }

                if (rules == 0)
                {
                    await RollBackAsync().ConfigureAwait(false);
                    return Fail("Windows would not accept the name resolution policy. Unblocking stays off.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                await RollBackAsync().ConfigureAwait(false);
                return Fail("Writing the name resolution policy needs SYSTEM rights, which this process does not have.");
            }
            catch (Exception ex)
            {
                await RollBackAsync().ConfigureAwait(false);
                return Fail($"Could not write the name resolution policy: {ex.Message}");
            }

            // 5. The proof, against every canary that was lied about a moment ago.
            if (poisoned.Count > 0)
            {
                var (verified, answer, waitedMs, app) = await VerifyAsync(poisoned, ct).ConfigureAwait(false);
                if (!verified)
                {
                    await RollBackAsync().ConfigureAwait(false);
                    return Fail(
                        $"The policy was written but after {waitedMs} ms Windows still resolves " +
                        $"{app.Canary} to {answer}. Everything has been undone.");
                }

                _log($"Unblock verified after {waitedMs} ms: {app.Canary} now resolves to {answer}. " +
                     $"Services: {policy.Describe()} (from {policy.Source}).");
            }

            Enabled = true;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Enabled && _resolver is null) return;
            await RollBackAsync().ConfigureAwait(false);
            _log("Unblocking off.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Policy out first, then the resolver. The other order leaves a window - however short - in
    /// which Windows is sending the claimed names to a socket that has just closed.
    /// </summary>
    private async Task RollBackAsync()
    {
        NrptPolicy.Remove(_log);

        if (_resolver is not null)
        {
            await _resolver.DisposeAsync().ConfigureAwait(false);
            _resolver = null;
        }

        _active = UnblockPolicy.Empty;
        Enabled = false;
    }

    private string Fail(string message)
    {
        LastError = message;
        _log("Unblock: " + message);
        return message;
    }

    /// <summary>
    /// Waits for the policy to actually take, rather than asking once and giving up.
    ///
    /// The first version asked 94 ms after writing the rules and was told 127.0.0.1, so it undid a
    /// policy that was about to start working. Two things were wrong with that. The cache was not
    /// flushed immediately before the question - the self-test flushed and the service did not,
    /// which is exactly why one passed and the other failed on the same machine an hour apart. And
    /// a single immediate question assumes the DNS client re-reads its policy table synchronously
    /// with a registry write, which nothing promises. Measured after the fix: 213 ms.
    ///
    /// One canary passing is enough. They share a resolver and a mechanism, so if the policy took
    /// for one it took for all - and holding the whole feature hostage to a service whose canary has
    /// since stopped being poisoned would turn a stale row in a database into an outage.
    /// </summary>
    private async Task<(bool Verified, string Answer, long WaitedMs, UnblockApp App)> VerifyAsync(
        IReadOnlyList<UnblockApp> poisoned, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var attempt = 0;
        var answer = "nothing";

        var askedBefore = _resolver?.ScopedQueries ?? 0;

        // Three seconds, asked every 250 ms. Long enough for a policy reload that is not instant,
        // short enough that nobody notices, and it only runs on a line proven to be lying.
        while (clock.ElapsedMilliseconds < 3000)
        {
            attempt++;

            foreach (var app in poisoned)
            {
                var addresses = await ResolveThroughWindowsAsync(app.Canary).ConfigureAwait(false);
                answer = Describe(addresses);

                if (!LooksPoisoned(addresses)) return (true, answer, clock.ElapsedMilliseconds, app);
            }

            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // The one number that says WHICH half failed, and the reason this is logged rather than
        // reasoned about later. If the resolver was never asked, Windows ignored the policy and no
        // amount of waiting will change it. If it WAS asked and the answer is still the ISP's, the
        // policy works and the fault is ours - a forwarding decision, or DoH being unreachable.
        var asked = (_resolver?.ScopedQueries ?? 0) - askedBefore;
        _log($"Unblock: asked Windows {attempt} time(s) over {clock.ElapsedMilliseconds} ms, still {answer}. " +
             (asked == 0
                 ? "Windows never asked the resolver, so it did not apply the policy."
                 : $"Windows DID ask the resolver ({asked} time(s)), so the policy applied and the answer is ours."));

        return (false, answer, clock.ElapsedMilliseconds, poisoned[0]);
    }

    /// <summary>
    /// Asks the local resolver directly, bypassing Windows, so a failure here is the resolver's and
    /// not the policy's. The two are diagnosed separately because they fail for different reasons
    /// and the log has to say which.
    /// </summary>
    private async Task<bool> AnswersItselfAsync(UnblockPolicy policy, CancellationToken ct)
    {
        var canary = policy.Apps[0].Canary;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            var query = DnsWire.BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), canary);

            await socket.SendToAsync(query, SocketFlags.None,
                new IPEndPoint(LocalResolver.ListenAddress, 53), ct).ConfigureAwait(false);

            var buffer = new byte[DnsWire.MaxUdpMessage];

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var received = await socket.ReceiveFromAsync(
                buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token).ConfigureAwait(false);

            var parsed = DnsWire.Parse(buffer, received.ReceivedBytes);
            if (parsed.RCode != 0 || parsed.Addresses.Count == 0)
            {
                _log($"The unblock resolver answered {DnsWire.RCodeName(parsed.RCode)} for {canary} " +
                     $"with {parsed.Addresses.Count} address(es).");
                return false;
            }

            return !LooksPoisoned([.. parsed.Addresses]);
        }
        catch (Exception ex)
        {
            _log($"The unblock resolver did not answer its own query: {ex.Message}");
            return false;
        }
    }

    private static async Task<IPAddress[]> ResolveThroughWindowsAsync(string host)
    {
        // The flush is not optional and not a detail. Windows holds the poisoned answer with
        // whatever TTL the ISP chose, and a lookup that hits that cache reports the state of a few
        // seconds ago - the difference between this feature reporting success and undoing itself.
        DnsCache.Flush();

        try
        {
            return await System.Net.Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The shape of a sinkhole, not a list of them.
    ///
    /// On the line this was measured on the answer is 127.0.0.1, but the test is deliberately the
    /// general one - loopback, unspecified, or an address that cannot route to a CDN - because the
    /// next ISP will choose a different sinkhole and a hard-coded 127.0.0.1 would miss it. An empty
    /// answer counts too: NXDOMAIN for a site that exists is a block, not a typo.
    /// </summary>
    private static bool LooksPoisoned(IPAddress[] addresses)
    {
        if (addresses.Length == 0) return true;

        foreach (var address in addresses)
        {
            if (IPAddress.IsLoopback(address)) continue;
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) continue;

            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
            {
                if (bytes[0] == 10) continue;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) continue;
                if (bytes[0] == 192 && bytes[1] == 168) continue;
                if (bytes[0] == 169 && bytes[1] == 254) continue;
            }

            return false;   // at least one address could plausibly be the real site
        }

        return true;
    }

    private static string Describe(IPAddress[] addresses) =>
        addresses.Length == 0 ? "nothing" : string.Join(", ", addresses.Select(a => a.ToString()));

    public async ValueTask DisposeAsync()
    {
        try { await DisableAsync().ConfigureAwait(false); }
        catch (Exception) { }
        _gate.Dispose();
    }
}
