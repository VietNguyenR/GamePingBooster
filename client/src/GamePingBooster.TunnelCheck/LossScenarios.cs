using System.Diagnostics;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// A relay's packet loss, measured the two ways relay comparisons read it (RelayLoss, 2026-09-29): a burst of pings
/// down a tunnel not yet pumping (TunnelClient.MeasureRelayBurstAsync), and the last minute of a live tunnel's pings
/// (TunnelClient.RecentLoss) - against a relay that drops a known share of them.
/// </summary>
internal static partial class Program
{
    private static async Task RelayLossIsMeasured()
    {
        // 1. A clean relay: every ping answered, in about the time the three pings it replaces took.
        using (var relay = new FakeRelay(Psk))
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 201);
            await tunnel.MeasureRelayBurstAsync(CancellationToken.None);   // warm-up: JIT, first sends
            var clock = Stopwatch.StartNew();
            var (best, loss) = await tunnel.MeasureRelayBurstAsync(CancellationToken.None);
            var ms = clock.Elapsed.TotalMilliseconds;
            Check($"Clean relay: {loss.Answered} of {loss.Sent} answered, not lossy, best {best:F1} ms",
                loss.Sent == RelayLoss.BurstPings && loss.Answered == loss.Sent && !loss.IsLossy && best is < 5, $"{loss}, best {best}");
            Check($"  and it ends when the last answer is in, not at the wait: {ms:F0} ms",
                ms < RelayLoss.BurstWaitMs, $"{ms:F0} ms");
        }

        // 2. One ping in four dropped - Da Nang from the owner's line was 26%.
        using (var relay = new FakeRelay(Psk))
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 202);
            var before = Interlocked.Read(ref relay.Pings);
            relay.DropPing = n => (n - before) % 4 == 0;
            var clock = Stopwatch.StartNew();
            var (best, loss) = await tunnel.MeasureRelayBurstAsync(CancellationToken.None);
            var ms = clock.Elapsed.TotalMilliseconds;
            Check($"One in four dropped: {loss}, lossy", loss.Sent == 16 && loss.Lost == 4 && loss.IsLossy, loss.ToString());
            Check("  the best round trip still comes from the answers", best is < 5, $"{best}");
            Check($"  and it waits for the lost ones no longer than {RelayLoss.BurstWaitMs} ms after the last ping: {ms:F0} ms",
                ms < RelayLoss.BurstWaitMs + 300, $"{ms:F0} ms");
            Check("  counted in the tunnel's own loss figure (the newest ping left out as in flight: 3 of 15)",
                tunnel.LossRatio is { } r && Math.Abs(r - 3.0 / 15) < 0.01,
                $"{tunnel.LossRatio}");
        }

        // 3. One ping lost is noise on any line.
        using (var relay = new FakeRelay(Psk))
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 203);
            var before = Interlocked.Read(ref relay.Pings);
            relay.DropPing = n => n - before == 5;
            var (_, loss) = await tunnel.MeasureRelayBurstAsync(CancellationToken.None);
            Check($"One ping of sixteen dropped: {loss}, not lossy", loss.Lost == 1 && !loss.IsLossy, loss.ToString());
        }

        // 4. A live tunnel: the downlink thread takes the pongs, and RecentLoss reads what it counted.
        using (var relay = new FakeRelay(Psk))
        using (var rig = new Rig())
        {
            var tunnel = await rig.StartAsync(relay, 204);
            for (var i = 0; i < 20; i++)
            {
                tunnel.SendQualityPing();
                await Task.Delay(10);
            }
            await Task.Delay(1200);
            var clean = tunnel.RecentLoss();
            Check($"Live tunnel, nothing dropped: {clean}", clean.Sent >= 20 && clean.Lost == 0, clean.ToString());

            relay.DropPing = n => n % 3 == 0;
            for (var i = 0; i < 45; i++)
            {
                tunnel.SendQualityPing();
                await Task.Delay(10);
            }
            await Task.Delay(600);
            var recent = tunnel.RecentLoss();
            // Forty-five pings a third of which are dropped, after twenty clean ones and beside a keepalive or two.
            Check($"Live tunnel, then one in three dropped: {recent}, lossy", recent.IsLossy && recent.Lost is >= 13 and <= 18,
                recent.ToString());
        }
    }
}
