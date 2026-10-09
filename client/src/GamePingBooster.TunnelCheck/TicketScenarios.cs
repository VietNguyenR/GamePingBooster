using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Measurement tickets against a REAL licensed relayd: the relay list's ping (RelayMeter), end to end. A licensed relay C
/// in WSL with an entry G in front of it by DNAT, a licence key pair of the rig's own and a token minted for a device
/// key made here - nothing of production's.
///
///     wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh ticket up
///     dotnet run --project client/src/GamePingBooster.TunnelCheck -- ticket
///
/// What it proves: a ticket is given and opens no session; every way is timed through relayd; DoorChoice moves the
/// list to the entry once the relay's own road is slow; the measured socket is handed over and a real handshake on it
/// succeeds on the same source port; and a relay that gives no ticket (the PSK relay A, when the rig is up) is left to
/// ICMP within a second rather than holding the list up.
/// </summary>
internal static partial class Program
{
    private static int TicketMain(string[] args)
    {
        var repo = FindRepo();
        _rigScript = Path.Combine(repo, "tools", "multi-tunnel-rig", "rig.sh");
        var status = RigShell("ticket status");
        var c = System.Text.RegularExpressions.Regex.Match(status, @"C=(\S+):51820");
        var g = System.Text.RegularExpressions.Regex.Match(status, @"G=(\S+):51820");
        var key = System.Text.RegularExpressions.Regex.Match(status, @"relay key: ([0-9a-f]{130})");
        if (!c.Success || !key.Success || !status.Contains("licensed relayd running: 1"))
        {
            Console.WriteLine("The licensed relay is not up. Run: wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh ticket up");
            Console.WriteLine(status);
            return 2;
        }
        var relayC = new IPEndPoint(IPAddress.Parse(c.Groups[1].Value), 51820);
        var entryG = new IPEndPoint(IPAddress.Parse(g.Groups[1].Value), 51820);
        var relayKey = key.Groups[1].Value;
        Console.WriteLine($"Licensed relay C {relayC}, entry G {entryG}.");

        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = MintRigToken(device);
        Console.WriteLine($"Token minted for a fresh device key ({token.Length} bytes).");

        var relay = new RelayEntry
        {
            Id = "c", Name = "C", Endpoint = relayC.ToString(), PublicKey = relayKey,
            Entries = [new RelayEntryPoint { Id = "g", Endpoint = entryG.ToString() }],
        };

        try
        {
            TicketScenario(relay, relayC, entryG, relayKey, device, token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  FAIL  the scenario threw: {ex}");
        }
        finally
        {
            RigShell("heal");
        }

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All ticket checks passed.");
            return 0;
        }
        Console.WriteLine($"{_failures} ticket check(s) FAILED. The client's log from the run:");
        foreach (var line in Log.TakeLast(40)) Console.WriteLine($"    {line}");
        Console.WriteLine(RigShell("ticket logs 30"));
        return 1;
    }

    private static async Task TicketScenario(RelayEntry relay, IPEndPoint relayC, IPEndPoint entryG, string relayKey, ECDsa device, byte[] token)
    {
        RigShell("heal");
        var connectsBefore = TicketRelayConnects();
        using var meter = new RelayMeter(() => new RelayMeter.Credentials(token, device, 0x7465737474696b74), Log.Enqueue);

        Console.WriteLine();
        Console.WriteLine("Both ways measured through relayd, with no session:");
        var measuring = meter.Measure([relay]);
        await meter.WaitAsync(measuring, 6, TimeSpan.FromSeconds(4), CancellationToken.None);
        Check("a ticket is given", meter.HasTicket("c"), "no ticket from the licensed relay");
        var calm = meter.Pick("c");
        Check("every way answered", calm is { } r && r.Readings.All(w => w.MedianMs is not null),
            calm is null ? "no pick" : string.Join(", ", calm.Value.Readings));
        Check("level ways - the relay's own address (an entry is a detour)", calm?.Pick.Door?.DoorId == "c", calm?.Pick.Reason ?? "no pick");
        Check("no session was opened for it", TicketRelayConnects() == connectsBefore, "relayd logged a client connecting");

        Console.WriteLine();
        Console.WriteLine("The relay's own road 30 ms slower:");
        RigShell("degrade c 30");
        // The window is five seconds of medians; the list keeps asking every 2.5 s, which keeps the relay measured. A list
        // that takes longer than one window and a bit to follow the line is the "list does not update" of 2026-10-09.
        for (var i = 0; i < 3; i++)
        {
            meter.Measure([relay]);
            await Task.Delay(2000);
        }
        var slow = meter.Pick("c");
        Check("the list moves to the entry", slow?.Pick.Door?.DoorId == "g", slow?.Pick.Reason ?? "no pick");
        var direct = slow?.Readings.FirstOrDefault(w => w.Direct)?.MedianMs;
        var entry = slow?.Readings.FirstOrDefault(w => !w.Direct)?.MedianMs;
        Check("and shows the entry's number, ~30 ms under the road's", direct - entry is > 20 and < 40, $"direct {direct:F1}, entry {entry:F1}");

        Console.WriteLine();
        Console.WriteLine("A connect takes the socket the entry was measured on:");
        var socket = meter.Take("c", "g", entryG);
        Check("the measured socket is handed over", socket is not null, "Take returned null");
        var port = (socket?.LocalEndPoint as IPEndPoint)?.Port;
        using (var auth = TunnelAuth.FromToken(token, device, relayKey))
        {
            using var client = new TunnelClient(entryG, auth, 0x7465737474696b74, Log.Enqueue);
            var session = await client.HandshakeAsync(attempts: 2, CancellationToken.None, socket);
            Check("a real handshake succeeds on it", session.SessionId != 0, "no session");
            Check("on the same source port - the lane measured", client.LocalPort == port, $"measured on {port}, tunnel on {client.LocalPort}");
            Check("relayd opened exactly one session, the tunnel's", TicketRelayConnects() == connectsBefore + 1,
                $"{TicketRelayConnects() - connectsBefore} connect(s) logged");
            var rtt = await client.MeasureRelayRttAsync(attempts: 3, CancellationToken.None);
            Check("the tunnel's ping is the entry's number", rtt is { } ms && entry is { } e && Math.Abs(ms - e) < 8, $"tunnel {rtt:F1}, list {entry:F1}");
        }

        // The way taken is measured again on a socket of its own, so a list still open goes on showing it.
        meter.Measure([relay]);
        await Task.Delay(1500);
        var again = meter.Pick("c");
        Check("the way taken goes on being measured, on a fresh socket", again?.Pick.Door?.DoorId == "g" &&
            again.Value.Readings.All(w => w.MedianMs is not null), again is { } p ? string.Join(", ", p.Readings) : "no pick");

        // The list closed long enough for the relay to go idle (sockets closed, ticket kept), then opened again: the
        // first reply must wait for the new sockets' answers. It once returned at once - a way not yet open counted as
        // settled - and showed ICMP, or "the relay's own address did not answer" from an empty window.
        Console.WriteLine();
        Console.WriteLine("The list opened again after the relay went idle:");
        await Task.Delay(RelayMeter.IdleAfter + TimeSpan.FromSeconds(1));
        var reopened = Stopwatch.StartNew();
        await meter.WaitAsync(meter.Measure([relay]), 3, TimeSpan.FromMilliseconds(1500), CancellationToken.None);
        var first = meter.Pick("c");
        Check("its first reply has every way measured", first is { } f && f.Readings.All(w => w.MedianMs is not null),
            first is { } q ? string.Join(", ", q.Readings) : "no pick (ICMP shown)");
        Check("and still the entry", first?.Pick.Door?.DoorId == "g", first?.Pick.Reason ?? "no pick");
        Check("within a second", reopened.Elapsed < TimeSpan.FromSeconds(1.2), $"{reopened.Elapsed.TotalSeconds:F1} s");

        // A relay that gives no ticket: the PSK relay A when the rig is up - an older relayd answers the same way.
        if (System.Text.RegularExpressions.Regex.Match(RigShell("status"), @"A=(\S+):51820") is { Success: true } a &&
            RigShell("status").Contains("relayd running: 2"))
        {
            Console.WriteLine();
            Console.WriteLine("A relay that gives no ticket (the rig's PSK relay A):");
            var psk = new RelayEntry { Id = "a", Name = "A", Endpoint = $"{a.Groups[1].Value}:51820", PublicKey = relayKey };
            var started = Stopwatch.GetTimestamp();
            await meter.WaitAsync(meter.Measure([psk]), 6, TimeSpan.FromSeconds(4), CancellationToken.None);
            var waited = Stopwatch.GetElapsedTime(started);
            Check("no ticket, no pick - the list shows ICMP for it", meter.Pick("a") is null && !meter.HasTicket("a"), "a pick from a PSK relay");
            Check("and is not held up waiting for one", waited < TimeSpan.FromSeconds(1.6), $"waited {waited.TotalSeconds:F1} s");
        }
    }

    private static int TicketRelayConnects() =>
        System.Text.RegularExpressions.Regex.Matches(RigShell("ticket logs 100000"), @"client connected").Count;

    /// <summary>The device's public key to a file WSL can read, a token minted for it by the rig's licence key.</summary>
    private static byte[] MintRigToken(ECDsa device)
    {
        var q = device.ExportParameters(false).Q;
        var pub = Convert.ToHexString([0x04, .. q.X!, .. q.Y!]).ToLowerInvariant();
        var file = Path.Combine(Path.GetTempPath(), $"gpb-ticket-device-{Environment.ProcessId}.pub");
        File.WriteAllText(file, pub);
        try
        {
            var wslPath = "/mnt/" + char.ToLowerInvariant(file[0]) + file[2..].Replace('\\', '/');
            var output = RigShell($"ticket mint {wslPath}");
            var hex = System.Text.RegularExpressions.Regex.Match(output, "[0-9a-fA-F]{100,}");
            if (!hex.Success) throw new InvalidOperationException($"licence-gen mint printed no token: {output}");
            return Convert.FromHexString(hex.Value);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
