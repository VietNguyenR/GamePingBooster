namespace GamePingBooster.Service.Dns;

/// <summary>
/// A TLS ClientHello rewritten as two TLS records, cut in the middle of the server name - so a filter that reads the
/// name out of the first record of the first segment never sees it whole.
///
/// Measured 2026-10-06 (tools\Probe-SniSplit.ps1, FPT Ha Noi AS18403 and the Viettel dev line): FPT resets every
/// handshake naming a PUBG or Steam API on every foreign edge, 7-12 ms after the hello, from inside its own network.
/// The same hello as two records passed 100% (3/3 per pair, about 30 pairs), and so did cutting the first TCP segment
/// after 2 bytes. Cutting the TCP segment in the middle of the name (one record) was reset every time on both ISPs:
/// the filter parses the record header of the first segment, and reassembles neither segments nor records.
///
/// Two records in ONE write are what is sent, not two segments: nothing depends on how the stack cuts it, and a TLS
/// server must accept a handshake message spread over records (RFC 8446 5.1). The live test with PUBG's own lobby
/// (2026-10-06, FPT, tools\Test-SniSplitLobby.ps1) loaded 8.5 MB from prod-live-front this way in ~5 s, 0 resets,
/// against 30 s more through the relay's 256 KB/s.
/// </summary>
internal static class SplitHello
{
    /// <summary>The longest first record read from a client: a hello is far smaller, a post-quantum one ~1.8 KB.</summary>
    public const int MaxRecord = 16384 + 2048;

    private static int Be16(ReadOnlySpan<byte> b, int p) => (b[p] << 8) | b[p + 1];

    /// <summary>
    /// Where the server name sits inside one whole ClientHello record, or false - not a handshake record, not a
    /// ClientHello, no server_name extension, or a record that is not exactly <paramref name="record"/>.
    /// </summary>
    public static bool FindName(ReadOnlySpan<byte> record, out int start, out int length)
    {
        start = length = 0;
        try
        {
            if (record.Length < 9 || record[0] != 0x16 || record[5] != 0x01 || Be16(record, 3) + 5 != record.Length) return false;
            var p = 9 + 2 + 32;            // record header, handshake header, version, random
            p += 1 + record[p];            // session id
            p += 2 + Be16(record, p);      // cipher suites
            p += 1 + record[p];            // compression methods
            var end = p + 2 + Be16(record, p);
            p += 2;
            while (p + 4 <= end)
            {
                int type = Be16(record, p), len = Be16(record, p + 2);
                if (type == 0)
                {
                    // server_name: list length (2), name type (1), name length (2), name.
                    length = Be16(record, p + 7);
                    start = p + 9;
                    return length > 1 && start + length <= record.Length;
                }
                p += 4 + len;
            }
        }
        catch (IndexOutOfRangeException) { }
        return false;
    }

    /// <summary>The server name of a whole ClientHello record, or null.</summary>
    public static string? NameOf(ReadOnlySpan<byte> record) =>
        FindName(record, out var start, out var length)
            ? System.Text.Encoding.ASCII.GetString(record.Slice(start, length)).TrimEnd('.').ToLowerInvariant()
            : null;

    /// <summary>
    /// The record as two records cut in the middle of the name, or the record unchanged when it has no name to cut.
    /// </summary>
    public static byte[] Split(ReadOnlySpan<byte> record)
    {
        if (!FindName(record, out var start, out var length)) return record.ToArray();

        var mid = start + length / 2;
        var first = mid - 5;                 // handshake bytes in record one
        var second = record.Length - mid;    // and in record two
        var split = new byte[5 + first + 5 + second];
        Header(split, 0, record, first);
        record[5..mid].CopyTo(split.AsSpan(5));
        Header(split, 5 + first, record, second);
        record[mid..].CopyTo(split.AsSpan(10 + first));
        return split;
    }

    private static void Header(byte[] into, int at, ReadOnlySpan<byte> record, int length)
    {
        into[at] = 0x16;
        into[at + 1] = record[1];
        into[at + 2] = record[2];
        into[at + 3] = (byte)(length >> 8);
        into[at + 4] = (byte)length;
    }
}

/// <summary>
/// Passes everything through, except that the first write - SslStream's ClientHello - goes out split. For the probes
/// that decide whether a name may go over the line split (EdgeProber.HandshakeAsync).
/// </summary>
internal sealed class SplitHelloStream(Stream inner) : Stream
{
    private bool _first = true;

    private ReadOnlyMemory<byte> Rewrite(ReadOnlyMemory<byte> buffer)
    {
        if (!_first) return buffer;
        _first = false;
        return SplitHello.Split(buffer.Span);
    }

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(Rewrite(buffer.AsMemory(offset, count)).Span);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(Rewrite(buffer.ToArray()).Span);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        inner.WriteAsync(Rewrite(buffer.AsMemory(offset, count)), ct).AsTask();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(Rewrite(buffer), ct);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
