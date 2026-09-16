using System.Text;
using System.Text.RegularExpressions;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// The connection-quality records waiting to be sent: one file each, under
/// %ProgramData%\GamePingBooster\quality\outbox.
///
/// The service writes them and cannot send them - the licence server authenticates the upload with
/// the account's refresh token, which belongs to the person and lives in their own profile, out of
/// reach of LocalSystem. So the app asks for a batch over the pipe, uploads it, and acknowledges what
/// the server took. A record leaves this folder only on that acknowledgement, which is what makes a
/// crash, a closed app or a server that is down lose nothing.
///
/// One file per record rather than one appended file, because removal is per record and has to be
/// safe against a half-finished write: a file is written under a temporary name and renamed into
/// place, so a reader never sees half of one.
///
/// Bounded: past <see cref="MaxFiles"/> the oldest go first. A machine whose app never runs, or whose
/// account is signed out, would otherwise fill the disk one match at a time.
/// </summary>
internal static partial class QualityOutbox
{
    public const int MaxFiles = 500;

    private static readonly object Gate = new();
    private static volatile bool _enabled = true;

    internal static string DirectoryPath => Path.Combine(QualityFile.DirectoryPath, "outbox");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdShape();

    /// <summary>Whether new records are queued. Turning it off empties the queue as well.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Clear();
        }
    }

    public static bool IsId(string? id) => id is not null && IdShape().IsMatch(id);

    /// <summary>
    /// Queues one record, and returns how many of the oldest had to be dropped to stay under
    /// <see cref="MaxFiles"/> - so the caller can say so. Failures are swallowed: the local daily file
    /// still has the record.
    /// </summary>
    public static int Add(string id, ReadOnlySpan<byte> json)
    {
        if (!_enabled || !IsId(id)) return 0;

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{id}.json";
                var path = Path.Combine(DirectoryPath, name);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, json.ToArray());
                File.Move(tmp, path, overwrite: true);
                return Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// The oldest records, up to <paramref name="max"/> of them and about <paramref name="maxChars"/>
    /// characters in all - one pipe message carries the batch. Also returns how many were waiting.
    /// </summary>
    public static (List<QualityOutboxItem> Items, int Pending) Take(int max, int maxChars)
    {
        var items = new List<QualityOutboxItem>();
        lock (Gate)
        {
            var files = Files();
            var total = 0;
            foreach (var file in files)
            {
                if (items.Count >= max) break;
                var id = IdOf(file);
                if (id is null) continue;

                string json;
                try
                {
                    json = File.ReadAllText(file, Encoding.UTF8);
                }
                catch (IOException)
                {
                    continue;
                }

                if (items.Count > 0 && total + json.Length > maxChars) break;
                total += json.Length;
                items.Add(new QualityOutboxItem { Id = id, Json = json });
            }
            return (items, files.Count);
        }
    }

    /// <summary>Removes the records with these ids. Anything not shaped like an id is ignored.</summary>
    public static int Acknowledge(IEnumerable<string> ids)
    {
        var wanted = ids.Where(IsId).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return 0;

        var removed = 0;
        lock (Gate)
        {
            foreach (var file in Files())
            {
                if (IdOf(file) is not { } id || !wanted.Contains(id)) continue;
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                }
            }
        }
        return removed;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var file in Files())
            {
                try { File.Delete(file); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>Oldest first: the name starts with the UTC time it was queued.</summary>
    private static List<string> Files()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return [];
            return Directory.GetFiles(DirectoryPath, "*.json").Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int Trim()
    {
        var files = Files();
        var dropped = 0;
        for (var i = 0; i < files.Count - MaxFiles; i++)
        {
            try
            {
                File.Delete(files[i]);
                dropped++;
            }
            catch (IOException) { }
        }
        return dropped;
    }

    /// <summary>"20260915140312123-&lt;id&gt;.json" -> id, or null for anything else.</summary>
    private static string? IdOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        if (dash < 0) return null;
        var id = name[(dash + 1)..];
        return IsId(id) ? id : null;
    }
}
