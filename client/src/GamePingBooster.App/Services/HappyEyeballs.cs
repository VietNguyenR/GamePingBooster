using System.Net;
using System.Net.Sockets;

namespace GamePingBooster.App.Services;

/// <summary>
/// Connects to a host by racing its addresses, the way every browser does (RFC 8305, "Happy
/// Eyeballs"), instead of trying them one after another the way .NET does.
///
/// <b>Why this exists - measured on a customer's machine, 2026-09-11.</b> The line had IPv6, but
/// IPv6 packets to the licence server were silently dropped. The browser never noticed: it starts
/// IPv4 a quarter of a second after IPv6 and keeps whichever answers. .NET 9 does not race. It
/// takes the resolved addresses in order, IPv6 first on a machine that has IPv6, and waits out each
/// dead one until the TCP connect gives up - 22 s apiece, measured. The licence server has two IPv6
/// addresses, so every request spent 44 s before it tried an address that worked, and sign-in
/// failed with "did not answer within 30 seconds" on a server that answers in under one. Raising the
/// timeout would only have made every sign-in on that line take 45 seconds.
///
/// The browser's success is exactly what made this hard to find: the sign-in page loaded, the
/// callback arrived, and only the app's own request after it hung. tools\Test-LicenceConnection.ps1
/// is the test that tells the two apart.
///
/// What this does, per RFC 8305:
///
///   - addresses are interleaved by family, starting with whichever family the OS put first, so an
///     IPv6-preferring machine still prefers IPv6 - it just does not wait on it;
///   - each attempt starts <see cref="AttemptDelay"/> after the previous one, or at once when the
///     previous one fails;
///   - the first connection to complete wins and every other attempt is cancelled and closed.
///
/// On a healthy dual-stack line the first IPv6 attempt completes inside the delay and nothing else
/// is ever started, so the common case costs nothing.
/// </summary>
public static class HappyEyeballs
{
    /// <summary>
    /// How long an attempt gets to itself before the next address is tried alongside it.
    /// RFC 8305 recommends 250 ms, which is also what Chrome and Firefox use.
    /// </summary>
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Plugs into <see cref="SocketsHttpHandler.ConnectCallback"/>.</summary>
    public static async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        var socket = await ConnectAsync(addresses, endpoint.Port, AttemptDelay, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// Alternates address families, starting with the family of the first address.
    ///
    /// Keeping the OS's first choice first matters: the OS orders addresses by RFC 6724 and local
    /// policy, and a working IPv6 path should still be the one used. Only the waiting changes.
    /// </summary>
    public static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        var result = new List<IPAddress>(addresses.Count);
        if (addresses.Count == 0) return result;

        var preferred = addresses[0].AddressFamily;
        var first = addresses.Where(a => a.AddressFamily == preferred).ToList();
        var second = addresses.Where(a => a.AddressFamily != preferred).ToList();

        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) result.Add(first[i]);
            if (i < second.Count) result.Add(second[i]);
        }
        return result;
    }

    /// <summary>
    /// Races connections to <paramref name="addresses"/> and returns the first socket to connect.
    ///
    /// Cancelling <paramref name="ct"/> cancels every attempt and throws
    /// <see cref="OperationCanceledException"/> - LicenceClient's deadline depends on that to turn a
    /// server that is unreachable on every address into a timeout rather than a hang.
    /// </summary>
    public static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port,
        TimeSpan attemptDelay, CancellationToken ct)
    {
        var ordered = Interleave(addresses);
        if (ordered.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<Task<Socket>>();
        Exception? lastFailure = null;
        var next = 0;

        try
        {
            while (true)
            {
                if (next < ordered.Count)
                {
                    running.Add(AttemptAsync(ordered[next++], port, race.Token));
                }

                // Wait for an attempt to finish, or - while addresses remain - for the moment the
                // next one is due. With nothing left to start there is only the attempts to wait on.
                var due = next < ordered.Count
                    ? Task.Delay(attemptDelay, race.Token)
                    : Task.Delay(Timeout.Infinite, race.Token);

                while (true)
                {
                    var finished = await Task.WhenAny(running.Append<Task>(due)).ConfigureAwait(false);

                    if (finished == due)
                    {
                        // A delay only ends early by cancellation, and only the caller cancels the
                        // race while it is still undecided.
                        ct.ThrowIfCancellationRequested();
                        break;
                    }

                    var attempt = (Task<Socket>)finished;
                    running.Remove(attempt);

                    if (attempt.Status == TaskStatus.RanToCompletion)
                    {
                        var winner = attempt.Result;
                        race.Cancel();
                        foreach (var loser in running) CloseWhenDone(loser);
                        running.Clear();
                        return winner;
                    }

                    lastFailure = attempt.Exception?.InnerException ?? lastFailure;

                    // A failure does not wait for the delay: the next address starts now.
                    if (next < ordered.Count) break;

                    if (running.Count == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw lastFailure ?? new SocketException((int)SocketError.ConnectionRefused);
                    }
                }
            }
        }
        finally
        {
            // Releases the pending delay timers, and any attempt still connecting when this method
            // leaves by an exception.
            race.Cancel();
            foreach (var loser in running) CloseWhenDone(loser);
        }
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Closes an attempt that lost the race, whenever it finishes.
    ///
    /// Cancellation is not enough on its own: an attempt can complete in the instant between the
    /// winner being chosen and the cancel arriving, and that socket would otherwise stay open with
    /// nobody holding it. Reading the exception also keeps a failed loser from surfacing later as an
    /// unobserved task exception.
    /// </summary>
    private static void CloseWhenDone(Task<Socket> attempt) =>
        _ = attempt.ContinueWith(static t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose();
            else _ = t.Exception;
        }, TaskScheduler.Default);
}
