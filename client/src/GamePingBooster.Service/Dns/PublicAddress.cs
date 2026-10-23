using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// The address this machine reaches the internet from, asked of a public STUN server (RFC 5389): one UDP packet each
/// way, nothing about the machine sent. For <see cref="LineBlockWatch"/>, which must notice a move to another ISP that
/// nothing local shows - the owner's modems all sit at 192.168.1.1 and the PC's resolvers are set to 8.8.8.8, so
/// FPT -> Viettel kept the FPT warning (2026-10-23).
/// </summary>
internal static class PublicAddress
{
    private static readonly (string Host, int Port)[] Servers = [("stun.cloudflare.com", 3478), ("stun.l.google.com", 19302)];

    private const uint MagicCookie = 0x2112A442;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>The public IPv4 address, or null when no server answered.</summary>
    public static async Task<IPAddress?> AskAsync(CancellationToken ct)
    {
        foreach (var (host, port) in Servers)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                var server = (await System.Net.Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token)
                    .ConfigureAwait(false)).FirstOrDefault();
                if (server is null) continue;

                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                var request = new byte[20];
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), 0x0001);   // Binding Request, no attributes
                BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
                RandomNumberGenerator.Fill(request.AsSpan(8, 12));
                await socket.SendToAsync(request, new IPEndPoint(server, port), timeout.Token).ConfigureAwait(false);

                var reply = new byte[512];
                var got = await socket.ReceiveAsync(reply, timeout.Token).ConfigureAwait(false);
                if (Parse(reply.AsSpan(0, got), request.AsSpan(8, 12)) is { } address) return address;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }
        }
        return null;
    }

    /// <summary>XOR-MAPPED-ADDRESS, or the older MAPPED-ADDRESS, from a Binding Success Response to our transaction.</summary>
    private static IPAddress? Parse(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> transaction)
    {
        if (reply.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(reply) != 0x0101) return null;
        if (!reply.Slice(8, 12).SequenceEqual(transaction)) return null;

        var end = Math.Min(reply.Length, 20 + BinaryPrimitives.ReadUInt16BigEndian(reply[2..]));
        IPAddress? mapped = null;
        for (var at = 20; at + 4 <= end;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(reply[at..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(reply[(at + 2)..]);
            var value = reply.Slice(at + 4, Math.Min(length, end - at - 4));
            if (value.Length >= 8 && value[1] == 0x01)
            {
                var raw = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
                if (type == 0x0020) return new IPAddress(BinaryPrimitives.ReverseEndianness(raw ^ MagicCookie));
                if (type == 0x0001) mapped = new IPAddress(BinaryPrimitives.ReverseEndianness(raw));
            }
            at += 4 + ((length + 3) & ~3);
        }
        return mapped;
    }
}
