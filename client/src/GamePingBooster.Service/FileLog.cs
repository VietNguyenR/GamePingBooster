using System.Collections.Concurrent;
using System.Text;

namespace GamePingBooster.Service;

/// <summary>
/// Writes the service log to a file under %ProgramData%\GamePingBooster\logs.
///
/// Why a file and not the Windows event log: ServiceBase.EventLog needs a registered event
/// source, and `sc create` does not create one. Every call therefore threw, every throw was
/// swallowed, and the service ran for weeks writing its log into nothing. A user reporting a
/// problem had nothing to attach, and the developer had nothing to read. Writing a file sidesteps
/// the registration problem entirely.
///
/// Two properties matter more than anything else here:
///
/// 1. <b>Writing must never block the caller.</b> The tunnel's pump threads call the log on their
///    error paths, and a synchronous file write on the uplink thread adds disk latency directly to
///    a game packet - degrading the exact thing this software exists to improve. So callers only
///    hand the line to a bounded queue and a dedicated thread does the I/O.
///
/// 2. <b>It must never throw.</b> A full disk, a locked file or a deleted directory must cost the
///    log, never the tunnel.
///
/// When the queue is full, lines are dropped rather than waited on - but the count is reported in
/// the file as soon as there is room again, because a log with a silent hole in it is worse than
/// one that admits the hole.
/// </summary>
internal sealed class FileLog : IDisposable
{
    private const int MaxQueuedLines = 4096;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int KeepFiles = 5;

    private readonly string _directory;
    private readonly string _currentPath;
    private readonly BlockingCollection<string> _queue = new(MaxQueuedLines);
    private readonly Thread? _writer;

    private int _dropped;
    private bool _disposed;

    /// <summary>Where the log is being written, for the startup banner. Null if unavailable.</summary>
    public string? Path { get; }

    public FileLog(string? directory = null)
    {
        _directory = directory ?? System.IO.Path.Combine(ServiceConfig.DefaultDirectory, "logs");
        _currentPath = System.IO.Path.Combine(_directory, "gpb-service.log");

        try
        {
            Directory.CreateDirectory(_directory);
            // Prove the directory is writable now, while there is still somewhere to report it,
            // rather than discovering it on the first real message.
            using (var probe = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                probe.Flush();
            }
            Path = _currentPath;
        }
        catch (Exception)
        {
            // No log file available. The service still runs; it simply has nothing to write to.
            return;
        }

        _writer = new Thread(WriterLoop)
        {
            Name = "gpb-log",
            IsBackground = true,
            // Below the pump threads on purpose: logging must never compete with packet handling.
            Priority = ThreadPriority.BelowNormal,
        };
        _writer.Start();

        Write($"--- log opened {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} " +
              $"(times below are local, offset {DateTimeOffset.Now:zzz}) ---");
    }

    /// <summary>
    /// Queues one line. Never blocks, never throws - safe to call from a pump thread.
    /// </summary>
    public void Write(string message)
    {
        if (_writer is null || _disposed) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        if (!_queue.TryAdd(line))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>The delegate the rest of the service already expects.</summary>
    public Action<string> Sink => Write;

    private void WriterLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                var text = dropped > 0
                    ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  WARNING: {dropped} log line(s) dropped - " +
                      $"the log queue filled up{Environment.NewLine}{line}"
                    : line;

                Append(text);
            }
        }
        catch (Exception)
        {
            // The queue was disposed from under us, or something unrecoverable happened. Losing
            // the log is acceptable; taking the service down with it is not.
        }
    }

    private void Append(string text)
    {
        try
        {
            RotateIfNeeded();
            using var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(text);
            // Flush to the OS after every batch so a crashed or killed service still leaves the
            // lines that led up to it - which are the only ones anybody ever wants.
            writer.Flush();
        }
        catch (Exception)
        {
            // Disk full, file locked, directory removed. Nothing sensible to do from inside the
            // logger, and throwing here would kill the writer thread for good.
        }
    }

    /// <summary>
    /// Rolls the file over once it grows past the cap, keeping a few generations. Without this a
    /// reconnect loop on a bad night would fill the user's disk.
    /// </summary>
    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_currentPath);
            if (!info.Exists || info.Length < MaxFileBytes) return;

            var oldest = System.IO.Path.Combine(_directory, $"gpb-service.{KeepFiles}.log");
            if (File.Exists(oldest)) File.Delete(oldest);

            for (var i = KeepFiles - 1; i >= 1; i--)
            {
                var from = System.IO.Path.Combine(_directory, $"gpb-service.{i}.log");
                var to = System.IO.Path.Combine(_directory, $"gpb-service.{i + 1}.log");
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }
            File.Move(_currentPath, System.IO.Path.Combine(_directory, "gpb-service.1.log"), overwrite: true);
        }
        catch (IOException)
        {
            // Someone has the file open (the user reading it, most likely). Keep appending to the
            // current one; it will roll over on a later attempt.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_writer is null)
        {
            _queue.Dispose();
            return;
        }

        try
        {
            _queue.CompleteAdding();
            // Bounded wait: a stuck writer must not hold up service shutdown, because Windows
            // kills a service that takes too long to stop.
            _writer.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutdown must not throw.
        }
        finally
        {
            _queue.Dispose();
        }
    }
}
