namespace GamePingBooster.Service;

/// <summary>
/// Replaces a file so that a power cut leaves either the old contents or the new ones.
///
/// A temporary file and a rename are not enough on their own, and a customer's config.json proved it
/// (2026-10-05): the file was there at the right size and every byte of it was 0x00, so the service
/// could not parse its own configuration and Windows reported error 1067 on every start. NTFS journals
/// the rename but not the data, and File.WriteAllText returns once the bytes are in the cache. Lose power
/// before the cache is written out and the rename survives, pointing at clusters that were never filled.
/// Flushing the temporary file to the disk before the rename closes that window.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
