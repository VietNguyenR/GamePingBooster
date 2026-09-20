using System.Net;

namespace GamePingBooster.Core.Net;

/// <summary>
/// The smallest DNS message reader and writer this product needs.
///
/// Written rather than taken from a package, for the reason the rest of this repository gives: the
/// service publishes with Native AOT and pulls in as little as it can. But there is a second reason
/// that matters more here. <see cref="Dns.GetHostAddresses(string)"/> hands back an answer with the
/// evidence already thrown away - which server replied, how fast, whether a SECOND reply with the
/// same transaction id turned up behind the first. Injected answers are recognised by exactly those
/// details, so the exchange has to be built and read by hand.
///
/// Shared by two callers with different needs, which is why the surface is split the way it is:
///
///   the block check    builds a query and reads the addresses out of the reply
///   the resolver       reads only the QUESTION, then passes the message on untouched
///
/// The second one is the important one. A forwarder that decoded and re-encoded every message would
/// have to understand EDNS, DNSSEC records, every RR type and every future extension, and would
/// quietly corrupt whatever it did not understand. Reading the question and relaying the original
/// bytes cannot: anything this code does not recognise travels through it unread.
/// </summary>
public static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypeCname = 5;
    public const ushort TypeAaaa = 28;

    public const int HeaderLength = 12;

    /// <summary>The largest reply that fits a plain UDP exchange once EDNS has advertised a buffer.</summary>
    public const int MaxUdpMessage = 4096;

    /// <summary>A standard recursive query. The id is the caller's, so replies can be matched.</summary>
    public static byte[] BuildQuery(ushort id, string host, ushort type = TypeA)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var size = HeaderLength + 1 + 4;
        foreach (var label in labels) size += 1 + label.Length;

        var msg = new byte[size];
        msg[0] = (byte)(id >> 8);
        msg[1] = (byte)id;
        msg[2] = 0x01; // RD: ask the server to recurse, which is what a client does.
        msg[5] = 0x01; // QDCOUNT = 1

        var p = HeaderLength;
        foreach (var label in labels)
        {
            if (label.Length > 63) throw new FormatException($"label too long in '{host}'");
            msg[p++] = (byte)label.Length;
            foreach (var c in label) msg[p++] = (byte)c;
        }

        msg[p++] = 0;                     // root label
        msg[p++] = (byte)(type >> 8);
        msg[p++] = (byte)type;
        msg[p++] = 0;
        msg[p] = 1;                       // QCLASS = IN
        return msg;
    }

    /// <summary>
    /// Reads the question and nothing else - the name being asked about and the record type.
    ///
    /// This is all the resolver needs to decide where a message goes, and deliberately all it
    /// looks at. Returns false for anything malformed, for a message that is already a reply, and
    /// for the multi-question form nobody implements, all of which are then forwarded untouched
    /// rather than answered: being unable to classify a message is not a reason to break it.
    /// </summary>
    public static bool TryReadQuestion(ReadOnlySpan<byte> msg, out string name, out ushort type)
    {
        name = string.Empty;
        type = 0;

        if (msg.Length < HeaderLength) return false;
        if ((msg[2] & 0x80) != 0) return false;                       // QR set: a reply, not a query
        if (((msg[4] << 8) | msg[5]) != 1) return false;              // exactly one question

        var p = HeaderLength;
        var labels = new List<string>();

        while (p < msg.Length)
        {
            var length = msg[p];
            if (length == 0) { p++; break; }

            // A pointer in a question is not legal and is not something to guess at.
            if ((length & 0xC0) != 0) return false;
            if (p + 1 + length > msg.Length) return false;

            labels.Add(AsciiLower(msg.Slice(p + 1, length)));
            p += 1 + length;
        }

        if (labels.Count == 0 || p + 4 > msg.Length) return false;

        name = string.Join('.', labels);
        type = (ushort)((msg[p] << 8) | msg[p + 1]);
        return true;
    }

    /// <summary>The transaction id, which a forwarder needs to match a reply to the client waiting for it.</summary>
    public static ushort ReadId(ReadOnlySpan<byte> msg) =>
        msg.Length < 2 ? (ushort)0 : (ushort)((msg[0] << 8) | msg[1]);

    public static void WriteId(Span<byte> msg, ushort id)
    {
        if (msg.Length < 2) return;
        msg[0] = (byte)(id >> 8);
        msg[1] = (byte)id;
    }

    /// <summary>
    /// Turns a query into a reply carrying nothing but an error code, so a client that cannot be
    /// served gets an answer instead of a two-second wait followed by a retry.
    /// </summary>
    public static byte[] BuildFailure(ReadOnlySpan<byte> query, int rcode)
    {
        // The question section is echoed back, as a reply must, so the length is however much of
        // the original message the question occupied.
        var end = HeaderLength;
        while (end < query.Length && query[end] != 0)
        {
            if ((query[end] & 0xC0) != 0) break;
            end += 1 + query[end];
        }

        end = Math.Min(query.Length, end + 1 + 4);   // root label, QTYPE, QCLASS

        var reply = new byte[end];
        query[..end].CopyTo(reply);

        reply[2] = (byte)(0x80 | (reply[2] & 0x01));           // QR, keeping RD
        reply[3] = (byte)(0x80 | (rcode & 0x0F));              // RA, plus the code
        reply[6] = 0; reply[7] = 0;                            // ANCOUNT
        reply[8] = 0; reply[9] = 0;                            // NSCOUNT
        reply[10] = 0; reply[11] = 0;                          // ARCOUNT
        return reply;
    }

    /// <summary>
    /// Reads a reply. Tolerant on purpose: a truncated or malformed answer is itself a result worth
    /// reporting, so anything unreadable past the header ends the record loop instead of throwing.
    /// </summary>
    public static DnsMessage Parse(byte[] msg, int length)
    {
        if (length < HeaderLength)
        {
            throw new FormatException($"DNS reply is {length} bytes, shorter than a header");
        }

        var id = (ushort)((msg[0] << 8) | msg[1]);
        var flags = (msg[2] << 8) | msg[3];
        var rcode = flags & 0x0F;
        var truncated = (flags & 0x0200) != 0;
        var questions = (msg[4] << 8) | msg[5];
        var answers = (msg[6] << 8) | msg[7];

        var addresses = new List<IPAddress>();
        var cnames = new List<string>();

        var p = HeaderLength;
        for (var i = 0; i < questions && p < length; i++)
        {
            SkipName(msg, length, ref p);
            p += 4;
        }

        for (var i = 0; i < answers && p < length; i++)
        {
            SkipName(msg, length, ref p);
            if (p + 10 > length) break;

            var type = (msg[p] << 8) | msg[p + 1];
            p += 8;                                   // type(2) + class(2) + ttl(4)
            var rdLength = (msg[p] << 8) | msg[p + 1];
            p += 2;
            if (p + rdLength > length) break;

            if (type == TypeA && rdLength == 4)
            {
                addresses.Add(new IPAddress(new[] { msg[p], msg[p + 1], msg[p + 2], msg[p + 3] }));
            }
            else if (type == TypeAaaa && rdLength == 16)
            {
                addresses.Add(new IPAddress(msg.AsSpan(p, 16).ToArray()));
            }
            else if (type == TypeCname)
            {
                var q = p;
                var cname = ReadName(msg, length, ref q);
                if (cname.Length > 0) cnames.Add(cname);
            }

            p += rdLength;
        }

        return new DnsMessage(id, rcode, truncated, addresses, cnames);
    }

    public static string RCodeName(int rcode) => rcode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE{rcode}",
    };

    /// <summary>
    /// Lower-cased without a culture, because DNS names are ASCII and case-insensitive by the
    /// standard. <c>ToLowerInvariant</c> would do, but the service runs with InvariantGlobalization
    /// and this avoids the question entirely.
    /// </summary>
    private static string AsciiLower(ReadOnlySpan<byte> label)
    {
        Span<char> chars = stackalloc char[label.Length];
        for (var i = 0; i < label.Length; i++)
        {
            var c = (char)label[i];
            chars[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        return new string(chars);
    }

    private static void SkipName(byte[] msg, int length, ref int p)
    {
        // A compression pointer ends the name where it stands - the rest of the name lives
        // elsewhere in the message and is not part of this record's length.
        while (p < length)
        {
            var len = msg[p];
            if (len == 0) { p++; return; }
            if ((len & 0xC0) == 0xC0) { p += 2; return; }
            p += 1 + len;
        }
    }

    private static string ReadName(byte[] msg, int length, ref int p)
    {
        var parts = new List<string>();

        // A malformed message can point a name at itself. Cap the jumps rather than trusting the
        // wire: this code reads replies from a network that is, by hypothesis, tampering with them.
        var jumps = 0;
        while (p < length && jumps < 64)
        {
            var len = msg[p];
            if (len == 0) { p++; break; }

            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= length) break;
                var target = ((len & 0x3F) << 8) | msg[p + 1];
                p += 2;
                if (target >= length) break;
                p = target;
                jumps++;
                continue;
            }

            if (p + 1 + len > length) break;
            parts.Add(System.Text.Encoding.ASCII.GetString(msg, p + 1, len));
            p += 1 + len;
        }

        return string.Join('.', parts);
    }
}

public sealed record DnsMessage(
    ushort Id,
    int RCode,
    bool Truncated,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<string> CNames);
