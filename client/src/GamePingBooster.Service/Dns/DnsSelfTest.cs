using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Exercises the Steam resolver from a terminal, without installing anything.
///
///     gpb-service.exe --dns-selftest             the resolver only - no rights needed
///     gpb-service.exe --dns-selftest --policy    and the Windows policy - needs SYSTEM
///
/// The split is the point. The resolver half can be run by anyone, on any machine, and proves the
/// part this project wrote: that a scoped name comes back with a real address and everything else
/// is relayed to the ISP untouched. The policy half writes machine-wide state and can only be
/// tested as SYSTEM, which is exactly why <see cref="UnblockDns"/> verifies itself at runtime instead
/// of trusting that it works - but when a terminal IS elevated, this runs the same sequence with
/// the output visible, and always removes what it installed.
/// </summary>
internal static class DnsSelfTest
{
    private const string Unclaimed = "www.microsoft.com";

    /// <summary>
    /// The list this runs against.
    ///
    /// The built-in one, not the delivered one: a developer running this from a terminal has no
    /// profile, and the point of the check is the resolver and the policy rather than whatever a
    /// licence server currently says. What the service actually uses comes from the profile - see
    /// UnblockPolicy - and is reported in the log when it enables.
    /// </summary>
    private static readonly UnblockPolicy Policy = UnblockPolicy.Builtin;

    private static string Canary => Policy.Apps[0].Canary;

    /// <summary>
    /// The second scoped name, and not decoration: on 2026-09-22 this one resolved to a filtered
    /// Akamai edge while the canary resolved to a clean one, so the store hung for nineteen seconds
    /// while community was instant. Asking about only one scoped name would have missed that
    /// entirely - and did.
    /// </summary>
    private const string SecondClaimed = "store.steampowered.com";

    public static async Task<int> RunAsync(bool includePolicy, CancellationToken ct)
    {
        void Log(string message) => Console.WriteLine("  " + message);

        Console.WriteLine();
        Console.WriteLine("Unblock resolver self-test");
        Console.WriteLine();

        var upstream = AdapterDns.Read();
        Console.WriteLine("Upstream resolvers Windows is using: " + (upstream.Count == 0
            ? "(none - the resolver would refuse to start)"
            : string.Join(", ", upstream.Select(u => u.ToString()))));

        if (upstream.Count == 0) return 1;

        Console.WriteLine();
        Console.WriteLine($"Before: {Canary} through Windows -> {await ThroughWindowsAsync(Canary).ConfigureAwait(false)}");
        Console.WriteLine();

        Console.WriteLine($"Checking {Policy.Describe()} ({Policy.Source}).");
        Console.WriteLine();

        var resolver = new LocalResolver(upstream, Policy, Log);
        var failures = 0;

        try
        {
            resolver.Start();

            // The awkward name FIRST, and the order is the test.
            //
            // On 2026-09-22 this one resolved to a filtered edge while the canary resolved to a
            // clean one, so a check that asked about a single name missed it entirely. Asking it
            // first makes it harder still: nothing is known to work yet, so the resolver has to go
            // and find a usable edge rather than take one from a pool a previous query warmed.
            // That is exactly the case that failed on a player's machine while passing here.
            failures += await AskAsync(SecondClaimed, expectScoped: true, ct).ConfigureAwait(false);

            // Then the canary, which by now is likely a cache hit - and an unclaimed name, which
            // proves the forwarder relays rather than swallowing, so a machine using this resolver
            // still has working DNS for everything else.
            failures += await AskAsync(Canary, expectScoped: true, ct).ConfigureAwait(false);
            failures += await AskAsync(Unclaimed, expectScoped: false, ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"  scoped={resolver.ScopedQueries} forwarded={resolver.ForwardedQueries} " +
                              $"failed={resolver.FailedQueries} filtered-edges-dropped={resolver.RejectedEdges}");

            if (includePolicy)
            {
                Console.WriteLine();
                failures += await PolicyAsync(Log, ct).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("  The Windows policy was not touched. Re-run as SYSTEM with --policy to test it.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("  " + ex.Message);
            failures++;
        }
        finally
        {
            await resolver.DisposeAsync().ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "OK" : $"{failures} check(s) failed");
        Console.WriteLine();
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> PolicyAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            var rules = 0;
            foreach (var app in Policy.Apps)
            {
                rules += NrptPolicy.Install(app.Id, app.Namespaces, LocalResolver.ListenAddress.ToString(), log);
            }

            if (rules == 0)
            {
                Console.Error.WriteLine("  No rules were written.");
                return 1;
            }

            var after = await ThroughWindowsAsync(Canary).ConfigureAwait(false);
            Console.WriteLine($"  With the policy on: {Canary} through Windows -> {after}");

            // Removed whatever happened. This is a test, and a test that can leave machine-wide
            // DNS policy behind is a worse problem than the one it was written to find.
            NrptPolicy.Remove(log);

            var restored = await ThroughWindowsAsync(Canary).ConfigureAwait(false);
            Console.WriteLine($"  Policy removed:     {Canary} through Windows -> {restored}");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("  Writing the policy needs SYSTEM rights. Run this through psexec -s.");
            return 1;
        }
        catch (Exception ex)
        {
            NrptPolicy.Remove(log);
            Console.Error.WriteLine("  " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> AskAsync(string host, bool expectScoped, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            await socket.SendToAsync(DnsWire.BuildQuery(id, host), SocketFlags.None,
                new IPEndPoint(LocalResolver.ListenAddress, 53), ct).ConfigureAwait(false);

            var buffer = new byte[DnsWire.MaxUdpMessage];

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), timeout.Token).ConfigureAwait(false);

            var parsed = DnsWire.Parse(buffer, received.ReceivedBytes);
            var route = expectScoped ? "over DoH" : "forwarded to the ISP";

            if (parsed.Id != id)
            {
                Console.Error.WriteLine($"  {host,-24} wrong transaction id back - the reply is not ours");
                return 1;
            }

            if (parsed.RCode != 0 || parsed.Addresses.Count == 0)
            {
                Console.Error.WriteLine(
                    $"  {host,-24} {DnsWire.RCodeName(parsed.RCode)} with {parsed.Addresses.Count} address(es), {route}");
                return 1;
            }

            Console.WriteLine($"  {host,-24} {string.Join(", ", parsed.Addresses.Take(3))}  ({route})");

            if (expectScoped && parsed.Addresses.Any(IPAddress.IsLoopback))
            {
                Console.Error.WriteLine($"  {host,-24} came back as loopback - that is the ISP's answer, not DoH's");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {host,-24} {ex.Message}");
            return 1;
        }
    }

    private static async Task<string> ThroughWindowsAsync(string host)
    {
        DnsCache.Flush();

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            return addresses.Length == 0 ? "nothing" : string.Join(", ", addresses.Select(a => a.ToString()));
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
