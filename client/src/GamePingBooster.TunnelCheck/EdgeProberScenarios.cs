using GamePingBooster.Service.Dns;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// An edge must pass every handshake, not one (EdgeProber.EveryTimeAsync, 2026-10-16): FPT reset prod-live-images on
/// CloudFront 18.65.14.x on 17 of 20 connections, and one lucky probe handed that edge to the lobby. Offline: the
/// handshakes are stand-ins.
/// </summary>
internal static partial class Program
{
    private static async Task AnEdgeMustPassEveryHandshake()
    {
        var calls = 0;
        var connects = 0;
        var askedFirst = 0;
        Task<bool> Ok(bool first, CancellationToken _, Action connected)
        {
            Interlocked.Increment(ref calls);
            if (first) Interlocked.Increment(ref askedFirst);
            connected();
            return Task.FromResult(true);
        }
        var clean = await EdgeProber.EveryTimeAsync(Ok, CancellationToken.None, () => Interlocked.Increment(ref connects));
        Check("A clean edge passes all three handshakes", clean && calls == EdgeProber.Handshakes, $"{clean}, {calls} handshakes");
        Check("  connected is said once, the first handshake alone is the first", connects == 1 && askedFirst == 1,
            $"{connects} connects, {askedFirst} first");

        // One passes, one is reset, one hangs on the filter until cancelled - the shape FPT showed.
        var hungCancelled = false;
        var index = 0;
        async Task<bool> Mixed(bool first, CancellationToken ct, Action connected)
        {
            connected();
            switch (Interlocked.Increment(ref index))
            {
                case 1: return true;
                case 2: await Task.Delay(20, CancellationToken.None); return false;
                default:
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { hungCancelled = true; }
                    return false;
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lucky = await EdgeProber.EveryTimeAsync(Mixed, CancellationToken.None);
        Check("An edge that passes one handshake and is reset on another is refused", !lucky);
        Check("  settled by the reset, not by the hang", clock.ElapsedMilliseconds < 1000 && hungCancelled,
            $"{clock.ElapsedMilliseconds} ms, hang cancelled {hungCancelled}");
    }
}
