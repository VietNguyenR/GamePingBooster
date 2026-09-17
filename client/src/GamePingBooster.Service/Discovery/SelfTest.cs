using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// Works out how to read the events by sending packets whose every field is already known, and
/// looking at how they come back.
///
/// Three things about Microsoft-Windows-Kernel-Network are not safe to take on trust: whether
/// ports are written in network byte order, whether an IPv4 address is, and - on a receive -
/// whether "daddr" means the far end or this machine. Getting any of them wrong does not fail
/// loudly. It produces a table of real-looking addresses the game never talked to.
///
/// So the tool sends to itself over loopback, where it owns both ends and knows both ports, and
/// reads its own events back. Loopback addresses tell the byte order apart on their own
/// (127.0.0.1 reversed is 1.0.0.127), and two different ports tell the direction apart. If
/// loopback produces no events, one packet to TEST-NET-1 (192.0.2.1, reserved, answers nothing)
/// still settles sends; receives are then left uncounted rather than guessed.
/// </summary>
internal static class SelfTest
{
    private static readonly TimeSpan WaitPerAttempt = TimeSpan.FromSeconds(3);

    public static DecodingRules Run(FlowTable table, Action<string> log)
    {
        var queue = table.CaptureOwnEvents();
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var rules = TryLoopback(queue, log);
                if (rules is not null) return rules;
            }

            log("Loopback produced no usable events; falling back to one packet to TEST-NET-1.");
            return TryTestNet(queue)
                   ?? throw new InvalidOperationException(
                       "Microsoft-Windows-Kernel-Network delivered no event for this tool's own test packets. " +
                       "Without one the decoding cannot be verified, so nothing this tool reported could be trusted.");
        }
        finally
        {
            table.StopCapturingOwnEvents();
        }
    }

    private static DecodingRules? TryLoopback(ConcurrentQueue<RawUdpEvent> queue, Action<string> log)
    {
        queue.Clear();

        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var receiverPort = (ushort)((IPEndPoint)receiver.LocalEndPoint!).Port;
        var senderPort = (ushort)((IPEndPoint)sender.LocalEndPoint!).Port;

        // Two ports that read the same either way round cannot tell the byte order apart. Rare,
        // and a fresh pair of sockets fixes it.
        if (receiverPort == BinaryPrimitives.ReverseEndianness(receiverPort) ||
            senderPort == BinaryPrimitives.ReverseEndianness(senderPort) ||
            receiverPort == BinaryPrimitives.ReverseEndianness(senderPort))
        {
            return null;
        }

        var v6 = Socket.OSSupportsIPv6 ? OpenV6Pair() : null;

        var buffer = new byte[64];
        var deadline = Stopwatch.GetTimestamp() + (long)(WaitPerAttempt.TotalSeconds * Stopwatch.Frequency);
        RawUdpEvent? send = null, receive = null, send6 = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            sender.SendTo("gpb-etwwatch self-test"u8, new IPEndPoint(IPAddress.Loopback, receiverPort));
            if (receiver.Poll(TimeSpan.FromMilliseconds(100), SelectMode.SelectRead)) receiver.Receive(buffer);
            if (v6 is { } pair)
            {
                pair.Sender.SendTo("gpb-etwwatch self-test"u8, new IPEndPoint(IPAddress.IPv6Loopback, pair.ReceiverPort));
                if (pair.Receiver.Poll(TimeSpan.FromMilliseconds(10), SelectMode.SelectRead)) pair.Receiver.Receive(buffer);
            }

            Thread.Sleep(100);
            while (queue.TryDequeue(out var e))
            {
                if (!Involves(e, receiverPort, senderPort) && !(v6 is { } p && Involves(e, p.ReceiverPort, p.SenderPort))) continue;
                if (e.IsV6)
                {
                    if (e.Direction == NetDirection.Send) send6 ??= e;
                }
                else if (e.Direction == NetDirection.Send)
                {
                    send ??= e;
                }
                else
                {
                    receive ??= e;
                }
            }

            if (send is not null && receive is not null && (v6 is null || send6 is not null)) break;
        }

        try
        {
            if (send is not { } s) return null;

            // Direction and port order together: exactly one reading must put the receiver's port
            // on the far end of the send.
            var readings = new List<(bool Swap, bool RemoteIsDestination)>();
            foreach (var swap in new[] { false, true })
            {
                var d = Port(s.DportRaw, swap);
                var src = Port(s.SportRaw, swap);
                if (d == receiverPort && src == senderPort) readings.Add((swap, true));
                if (src == receiverPort && d == senderPort) readings.Add((swap, false));
            }
            if (readings.Count != 1)
            {
                log($"Self-test: the send event's ports ({s.DportRaw}, {s.SportRaw}) match {readings.Count} readings " +
                    $"of receiver {receiverPort} / sender {senderPort}; retrying.");
                return null;
            }
            var (swapPorts, sendRemoteIsDestination) = readings[0];

            var address = sendRemoteIsDestination ? s.DaddrLo : s.SaddrLo;
            bool swapIpv4;
            if ((uint)address == 0x7F000001) swapIpv4 = false;
            else if ((uint)address == 0x0100007F) swapIpv4 = true;
            else
            {
                log($"Self-test: the send event's address reads 0x{(uint)address:X8}, which is 127.0.0.1 in neither byte order.");
                return null;
            }

            bool? receiveRemoteIsDestination = null;
            if (receive is { } r)
            {
                // On a receive the far end is the sender.
                if (Port(r.DportRaw, swapPorts) == senderPort && Port(r.SportRaw, swapPorts) == receiverPort) receiveRemoteIsDestination = true;
                else if (Port(r.SportRaw, swapPorts) == senderPort && Port(r.DportRaw, swapPorts) == receiverPort) receiveRemoteIsDestination = false;
            }

            var ipv6Verified = false;
            if (send6 is { } s6 && v6 is { } pair6)
            {
                var hi = sendRemoteIsDestination ? s6.DaddrHi : s6.SaddrHi;
                var lo = sendRemoteIsDestination ? s6.DaddrLo : s6.SaddrLo;
                var port = Port(sendRemoteIsDestination ? s6.DportRaw : s6.SportRaw, swapPorts);
                ipv6Verified = hi == 0 && lo == 1 && port == pair6.ReceiverPort;
            }

            return new DecodingRules
            {
                SwapIpv4 = swapIpv4,
                SwapPorts = swapPorts,
                SendRemoteIsDestination = sendRemoteIsDestination,
                ReceiveRemoteIsDestination = receiveRemoteIsDestination,
                Ipv6Verified = ipv6Verified,
                Evidence = "loopback: " +
                           $"send far end = {(sendRemoteIsDestination ? "daddr/dport" : "saddr/sport")}, " +
                           $"ports {(swapPorts ? "network" : "host")} order, IPv4 {(swapIpv4 ? "reversed" : "network")} order, " +
                           $"receive far end = {receiveRemoteIsDestination switch { true => "daddr/dport", false => "saddr/sport", null => "UNVERIFIED (receives not counted)" }}, " +
                           $"IPv6 {(ipv6Verified ? "verified" : "unverified")}",
            };
        }
        finally
        {
            v6?.Dispose();
        }
    }

    private static DecodingRules? TryTestNet(ConcurrentQueue<RawUdpEvent> queue)
    {
        queue.Clear();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        const ushort remotePort = 9;   // discard
        var remote = new IPEndPoint(IPAddress.Parse("192.0.2.1"), remotePort);
        try
        {
            socket.SendTo("gpb-etwwatch self-test"u8, remote);
        }
        catch (SocketException)
        {
            return null;   // no IPv4 route at all
        }
        var localPort = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;

        var deadline = Stopwatch.GetTimestamp() + (long)(WaitPerAttempt.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(200);
            while (queue.TryDequeue(out var e))
            {
                if (e.IsV6 || e.Direction != NetDirection.Send) continue;
                foreach (var swapPorts in new[] { false, true })
                {
                    foreach (var remoteIsDestination in new[] { true, false })
                    {
                        var far = Port(remoteIsDestination ? e.DportRaw : e.SportRaw, swapPorts);
                        var near = Port(remoteIsDestination ? e.SportRaw : e.DportRaw, swapPorts);
                        if (far != remotePort || near != localPort) continue;

                        var address = (uint)(remoteIsDestination ? e.DaddrLo : e.SaddrLo);
                        bool? swapIpv4 = address switch { 0xC0000201 => false, 0x010200C0 => true, _ => null };
                        if (swapIpv4 is null) continue;

                        return new DecodingRules
                        {
                            SwapIpv4 = swapIpv4.Value,
                            SwapPorts = swapPorts,
                            SendRemoteIsDestination = remoteIsDestination,
                            ReceiveRemoteIsDestination = null,
                            Ipv6Verified = false,
                            Evidence = "TEST-NET-1 send only: receives are NOT counted, IPv6 unverified",
                        };
                    }
                }
            }
            socket.SendTo("gpb-etwwatch self-test"u8, remote);
        }
        return null;
    }

    private static bool Involves(in RawUdpEvent e, ushort a, ushort b)
    {
        foreach (var swap in new[] { false, true })
        {
            var d = Port(e.DportRaw, swap);
            var s = Port(e.SportRaw, swap);
            if ((d == a && s == b) || (d == b && s == a)) return true;
        }
        return false;
    }

    private static ushort Port(ushort raw, bool swap) => swap ? BinaryPrimitives.ReverseEndianness(raw) : raw;

    private sealed class V6Pair(Socket receiver, Socket sender) : IDisposable
    {
        public Socket Receiver { get; } = receiver;
        public Socket Sender { get; } = sender;
        public ushort ReceiverPort => (ushort)((IPEndPoint)Receiver.LocalEndPoint!).Port;
        public ushort SenderPort => (ushort)((IPEndPoint)Sender.LocalEndPoint!).Port;

        public void Dispose()
        {
            Receiver.Dispose();
            Sender.Dispose();
        }
    }

    private static V6Pair? OpenV6Pair()
    {
        Socket? receiver = null, sender = null;
        try
        {
            receiver = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            sender = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            receiver.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            sender.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return new V6Pair(receiver, sender);
        }
        catch (SocketException)
        {
            receiver?.Dispose();
            sender?.Dispose();
            return null;
        }
    }
}
