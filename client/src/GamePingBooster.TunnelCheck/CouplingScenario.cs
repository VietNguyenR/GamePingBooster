using System.Diagnostics;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The one cost of several downlinks writing to one adapter (docs/MULTI-TUNNEL.md 5.1): Wintun delivers in the order
/// packets were ALLOCATED, so a downlink thread descheduled between its allocate and its send holds back every other
/// tunnel's packet behind it. Measured here, not argued: the time from a relay sending a packet to "Windows" having
/// it, for home alone and then for three tunnels at a match's rate each, through the real downlinks, dispatcher and
/// rewrite.
/// </summary>
internal static partial class Program
{
    /// <summary>A busy match's rate, per tunnel.</summary>
    private const int CouplingPacketsPerSecond = 150;
    private const int CouplingSeconds = 5;

    private static async Task DownlinkCouplingAtThreeTunnels()
    {
        using var m = new MultiRig();
        using var third = new FakeRelay(Psk, firstInner: 150);
        await m.StartAsync(clientId: 47);
        var other2 = await m.Rig.OpenAsync(third, clientId: 47);
        other2.Dispatcher = m.Dispatcher;
        other2.StartPumping(m.Rig.Device, m.Rig.Cts.Token);

        var tunnels = new (FakeRelay Relay, TunnelClient Tunnel, uint Server)[]
        {
            (m.HomeRelay, m.Home, SgServer),
            (m.OtherRelay, m.Other, KrServer),
            (third, other2, Addr(34, 85, 0, 48)),
        };
        Check("Three tunnels, three different inner addresses - two of them rewritten on the way down",
            tunnels.Select(t => t.Tunnel.InnerIp).Distinct().Count() == 3);

        var alone = await MeasureDownlinks(m, tunnels[..1], firstSequence: 0);
        var together = await MeasureDownlinks(m, tunnels, firstSequence: 1_000_000);
        if (alone is null || together is null)
        {
            Check("Every packet reached Windows", false, alone is null ? "home alone lost some" : "three together lost some");
            return;
        }

        var added = together.Value.P99 - alone.Value.P99;
        Check($"Relay to Windows, {CouplingPacketsPerSecond} packets/s per tunnel for {CouplingSeconds} s - home alone: " +
              $"p50 {alone.Value.P50:F3} ms, p99 {alone.Value.P99:F3} ms; three tunnels: p50 {together.Value.P50:F3} ms, " +
              $"p99 {together.Value.P99:F3} ms (p99 {added:+0.000;-0.000} ms)",
            together.Value.P99 < 5, $"p99 {together.Value.P99:F3} ms with three tunnels");
        Check("Each tunnel's packets reach Windows in the order its relay sent them", together.Value.InOrder);
    }

    /// <summary>
    /// Each relay sends <see cref="CouplingPacketsPerSecond"/> a second to its client from its own thread, paced by the
    /// clock; returns the relay-to-Windows latency over every packet, or null if any never arrived.
    /// </summary>
    private static async Task<(double P50, double P99, bool InOrder)?> MeasureDownlinks(MultiRig m,
        (FakeRelay Relay, TunnelClient Tunnel, uint Server)[] tunnels, int firstSequence)
    {
        const int perTunnel = CouplingPacketsPerSecond * CouplingSeconds;
        var sentAt = new long[tunnels.Length * perTunnel];
        var before = m.Rig.Device.ToWindows.Count;
        var interval = Stopwatch.Frequency / CouplingPacketsPerSecond;

        var senders = tunnels.Select((t, index) => new Thread(() =>
        {
            var session = t.Relay.SessionOf(t.Tunnel.SessionId)!;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < perTunnel; i++)
            {
                var due = start + i * interval;
                while (Stopwatch.GetTimestamp() < due)
                {
                    if (due - Stopwatch.GetTimestamp() > Stopwatch.Frequency / 500) Thread.Sleep(1);
                    else Thread.SpinWait(50);
                }
                var slot = index * perTunnel + i;
                var packet = GameChecked(t.Server, t.Tunnel.InnerIp, firstSequence + slot);
                sentAt[slot] = Stopwatch.GetTimestamp();
                t.Relay.SendToClient(session, packet);
            }
        }) { IsBackground = true, Name = $"coupling relay {index}" }).ToList();

        foreach (var sender in senders) sender.Start();
        foreach (var sender in senders) sender.Join();
        if (!await WaitUntil(() => m.Rig.Device.ToWindows.Count - before >= sentAt.Length, 5_000)) return null;

        var arrived = m.Rig.Device.ToWindows.Skip(before)
            .Select(p => (Slot: SequenceOf(p.Packet) - firstSequence, p.At))
            .Where(p => p.Slot >= 0 && p.Slot < sentAt.Length)
            .ToList();
        if (arrived.Count != sentAt.Length) return null;

        var inOrder = arrived.GroupBy(p => p.Slot / perTunnel)
            .All(g => g.Select(p => p.Slot).SequenceEqual(g.Select(p => p.Slot).Order()));
        var ms = arrived.Select(p => (p.At - sentAt[p.Slot]) * 1000.0 / Stopwatch.Frequency).Order().ToList();
        return (ms[ms.Count / 2], ms[(int)(ms.Count * 0.99)], inOrder);
    }
}
