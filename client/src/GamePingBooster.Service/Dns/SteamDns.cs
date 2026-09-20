using System.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Turns Steam's DNS block on and off as one thing, and refuses to claim it worked when it did not.
///
/// The order matters and is the whole of the design:
///
///   1. read the resolvers Windows uses now, BEFORE anything is changed
///   2. ask whether this line actually poisons the canary name, so there is something to prove
///   3. start the local resolver and make sure it answers
///   4. only then point Windows at it
///   5. ask the canary again through Windows - if the answer did not change, undo everything
///
/// Step 5 is there because the mechanism in step 4 cannot be tested from a developer's terminal:
/// writing NRPT rules needs SYSTEM, and a rule that Windows quietly ignores looks identical to a
/// rule that works. Rather than ship a guess, the service proves it on the machine it is running on
/// and rolls back if the proof fails. A feature that reports "could not enable" is recoverable; one
/// that silently does nothing while the player waits for Steam to load is not.
///
/// Nothing here touches the tunnel, the routes or the relay. Steam is not a game being accelerated -
/// it is a name being answered honestly - and the two halves of the product share only a switch.
/// </summary>
internal sealed class SteamDns : IAsyncDisposable
{
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LocalResolver? _resolver;
    private bool _poisonedBefore;

    public SteamDns(Action<string> log) => _log = log;

    public bool Enabled { get; private set; }

    /// <summary>Why the last enable failed, or null. Shown in the status line, so it is user-facing prose.</summary>
    public string? LastError { get; private set; }

    /// <summary>One line for the status pipe, so the UI can say something true about this half of the product.</summary>
    public string Detail
    {
        get
        {
            if (!Enabled) return LastError ?? "off";

            var resolver = _resolver;
            return resolver is null
                ? "on"
                : $"on - {resolver.ScopedQueries} Steam name(s) answered, {resolver.ForwardedQueries} forwarded";
        }
    }

    /// <summary>
    /// Removes anything a previous run left behind. Called at service start, before any decision.
    ///
    /// A rule pointing at a resolver that is not listening is worse than no rule: Windows will send
    /// the scoped names to a dead address and Steam fails in a way that survives a reboot and has no
    /// visible cause. The service crashing must not be able to leave that behind, and the only place
    /// that can be guaranteed to run afterwards is the next start.
    /// </summary>
    public static void RemoveLeftovers(Action<string> log)
    {
        var removed = NrptPolicy.Remove(log);
        if (removed > 0) log($"Removed {removed} Steam DNS rule(s) left by a previous run.");
    }

    /// <summary>
    /// Keeps trying to turn the fix on, for as long as the service runs, and stops once it is on.
    ///
    /// A loop and not a single attempt at startup, because the service starts with Windows and the
    /// network usually does not exist yet at that moment: there are no resolvers to read and no DoH
    /// to reach, so the first attempt on a cold boot fails for a reason that has nothing to do with
    /// this machine's ISP. One try would mean Steam stays blocked until the next reboot.
    ///
    /// It does NOT re-check once enabled. What would deserve re-checking is the machine moving to
    /// another network - the forwarder would then still hold the old resolvers, which matters for
    /// the handful of excluded names like media.steampowered.com that are forwarded rather than
    /// resolved here. That is a real gap and a later job; it is written down rather than guessed at.
    /// </summary>
    public void Start(CancellationToken ct) => _ = Task.Run(async () =>
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
                _log($"Steam DNS: {ex.Message}");
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Backing off to a minute: the common failure is a network that is not up yet, which
            // resolves itself in seconds, and the uncommon one is a machine that will never manage
            // it - which must not keep writing a log line every five seconds all day.
            delay = TimeSpan.FromMinutes(1);
        }
    }, ct);

    /// <summary>Returns null when it worked, or a sentence for the user when it did not.</summary>
    public async Task<string?> EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Enabled) return null;

            LastError = null;

            // 1. The upstreams, before the policy can point them at us.
            var upstream = AdapterDns.Read();
            if (upstream.Count == 0)
            {
                return Fail("Windows has no resolver of its own to forward to. Steam support stays off.");
            }

            // 2. Is there anything to prove? Asked before the resolver exists, so the answer is the
            //    line's own. A clean line is not a failure - it just means step 5 cannot verify, and
            //    says so rather than inventing a result.
            _poisonedBefore = LooksPoisoned(await ResolveThroughWindowsAsync(SteamNames.Canary).ConfigureAwait(false));
            _log(_poisonedBefore
                ? $"This line poisons {SteamNames.Canary}, so the fix can be verified after it is applied."
                : $"This line answers {SteamNames.Canary} honestly - nothing to verify against, enabling anyway.");

            // 3. The resolver first. Pointing Windows at an address nothing is listening on would
            //    take DNS down for the scoped names with no way back except a reboot.
            var resolver = new LocalResolver(upstream, _log);
            try
            {
                resolver.Start();
            }
            catch (Exception ex)
            {
                await resolver.DisposeAsync().ConfigureAwait(false);
                return Fail($"Could not start the Steam resolver: {ex.Message}");
            }

            if (!await AnswersItselfAsync(ct).ConfigureAwait(false))
            {
                await resolver.DisposeAsync().ConfigureAwait(false);
                return Fail("The Steam resolver started but did not answer its own query.");
            }

            _resolver = resolver;

            // 4. Now Windows can be pointed at it.
            try
            {
                var rules = NrptPolicy.Install(SteamNames.Namespaces, LocalResolver.ListenAddress.ToString(), _log);
                if (rules == 0)
                {
                    await RollBackAsync().ConfigureAwait(false);
                    return Fail("Windows would not accept the name resolution policy. Steam support stays off.");
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

            // 5. The proof.
            if (_poisonedBefore)
            {
                var (verified, answer, waitedMs) = await VerifyAsync(ct).ConfigureAwait(false);
                if (!verified)
                {
                    await RollBackAsync().ConfigureAwait(false);
                    return Fail(
                        $"The policy was written but after {waitedMs} ms Windows still resolves " +
                        $"{SteamNames.Canary} to {answer}. Everything has been undone.");
                }

                _log($"Steam DNS verified after {waitedMs} ms: {SteamNames.Canary} now resolves to {answer}.");
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
            _log("Steam DNS off.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Policy out first, then the resolver. The other order leaves a window - however short - in
    /// which Windows is sending the scoped names to a socket that has just closed.
    /// </summary>
    private async Task RollBackAsync()
    {
        NrptPolicy.Remove(_log);

        if (_resolver is not null)
        {
            await _resolver.DisposeAsync().ConfigureAwait(false);
            _resolver = null;
        }

        Enabled = false;
    }

    private string Fail(string message)
    {
        LastError = message;
        _log("Steam DNS: " + message);
        return message;
    }

    /// <summary>
    /// Asks the local resolver directly, bypassing Windows, so a failure here is the resolver's and
    /// not the policy's. The two are diagnosed separately because they fail for different reasons
    /// and the log has to say which.
    /// </summary>
    private async Task<bool> AnswersItselfAsync(CancellationToken ct)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);

            var query = Core.Net.DnsWire.BuildQuery(
                (ushort)Random.Shared.Next(1, ushort.MaxValue), SteamNames.Canary);

            await socket.SendToAsync(query, System.Net.Sockets.SocketFlags.None,
                new IPEndPoint(LocalResolver.ListenAddress, 53), ct).ConfigureAwait(false);

            var buffer = new byte[Core.Net.DnsWire.MaxUdpMessage];

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            var received = await socket.ReceiveFromAsync(
                buffer, System.Net.Sockets.SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), timeout.Token).ConfigureAwait(false);

            var parsed = Core.Net.DnsWire.Parse(buffer, received.ReceivedBytes);
            if (parsed.RCode != 0 || parsed.Addresses.Count == 0)
            {
                _log($"The Steam resolver answered {Core.Net.DnsWire.RCodeName(parsed.RCode)} " +
                     $"with {parsed.Addresses.Count} address(es).");
                return false;
            }

            return !LooksPoisoned([.. parsed.Addresses]);
        }
        catch (Exception ex)
        {
            _log($"The Steam resolver did not answer its own query: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Waits for the policy to actually take, rather than asking once and giving up.
    ///
    /// The first version asked 94 ms after writing the rules and was told 127.0.0.1, so it undid a
    /// policy that was about to start working. Two things were wrong with that. The cache was not
    /// flushed immediately before the question - the self-test flushed and the service did not,
    /// which is exactly why one passed and the other failed on the same machine an hour apart. And
    /// a single immediate question assumes the DNS client re-reads its policy table synchronously
    /// with a registry write, which nothing promises.
    ///
    /// So: flush, ask, and if the answer is still the ISP's, wait and ask again for a few seconds.
    /// The elapsed time is logged on success too, because how long this actually takes in the field
    /// is a fact worth having and nobody has measured it.
    /// </summary>
    private async Task<(bool Verified, string Answer, long WaitedMs)> VerifyAsync(CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var attempt = 0;
        var answer = "nothing";

        // The count BEFORE this loop, not zero: step 3 already asked the resolver the same question
        // directly, so the counter is never at zero by the time verification starts and a test for
        // zero would always say "Windows did not apply the policy", including when it had.
        var askedBefore = _resolver?.ScopedQueries ?? 0;

        // Three seconds, asked every 250 ms. Long enough for a policy reload that is not instant,
        // short enough that a player pressing Connect does not notice, and it only runs at all on
        // a line that was proven to be poisoned a moment ago.
        while (clock.ElapsedMilliseconds < 3000)
        {
            attempt++;
            var addresses = await ResolveThroughWindowsAsync(SteamNames.Canary).ConfigureAwait(false);
            answer = Describe(addresses);

            if (!LooksPoisoned(addresses)) return (true, answer, clock.ElapsedMilliseconds);

            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // The one number that says WHICH half failed, and the reason this is logged rather than
        // reasoned about later. If the resolver was never asked, Windows ignored the policy and no
        // amount of waiting will change it. If it WAS asked and the answer is still the ISP's, the
        // policy works and the fault is ours - a forwarding decision, or DoH being unreachable.
        // Without this line the two are indistinguishable from the outside and both look like
        // "Steam support is off".
        var asked = (_resolver?.ScopedQueries ?? 0) - askedBefore;
        _log($"Steam DNS: asked Windows {attempt} time(s) over {clock.ElapsedMilliseconds} ms, still {answer}. " +
             (asked == 0
                 ? "Windows never asked the resolver, so it did not apply the policy."
                 : $"Windows DID ask the resolver ({asked} time(s)), so the policy applied and the answer is ours."));

        return (false, answer, clock.ElapsedMilliseconds);
    }

    /// <summary>
    /// Asks Windows, with the cache emptied first.
    ///
    /// The flush is not optional and not a detail. Windows holds the poisoned answer with whatever
    /// TTL the ISP chose, and a lookup that hits that cache reports the state of a few seconds ago -
    /// which is the difference between this feature reporting success and undoing itself.
    /// </summary>
    private static async Task<IPAddress[]> ResolveThroughWindowsAsync(string host)
    {
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
    /// answer counts too: NXDOMAIN for steamcommunity.com is a block, not a typo.
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

            return false;   // at least one address could plausibly be Steam
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
