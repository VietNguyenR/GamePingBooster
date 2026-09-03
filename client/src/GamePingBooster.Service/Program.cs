using System.ServiceProcess;
using GamePingBooster.Service.Ipc;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.Service;

/// <summary>
/// Entry point for the Windows Service.
///
/// Why this must be a service running as LocalSystem rather than a UI app running as
/// Administrator: Wintun requires LocalSystem to create the virtual adapter - Administrator is
/// not enough. The split also buys two more things: the user never sees a UAC prompt when
/// opening the app, and a UI crash does not tear down a running tunnel.
///
/// Run in console mode for debugging:  gpb-service.exe --console
///
/// The installer calls two more, both as SYSTEM and both before any configuration exists:
///
///   gpb-service.exe --install-driver     put the Wintun driver in place during setup
///   gpb-service.exe --remove-driver      take it away again at uninstall
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Driver setup comes first and deliberately never touches ServiceConfig. The installer
        // calls these before config.json exists, and a missing file must not fail an install.
        if (args.Contains("--install-driver", StringComparer.OrdinalIgnoreCase))
        {
            return DriverSetup.Install(Console.Error.WriteLine);
        }
        if (args.Contains("--remove-driver", StringComparer.OrdinalIgnoreCase))
        {
            return DriverSetup.Remove(Console.Error.WriteLine);
        }

        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            return RunConsoleAsync().GetAwaiter().GetResult();
        }

        ServiceBase.Run(new BoosterService());
        return 0;
    }

    private static async Task<int> RunConsoleAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Console mode writes to both: the terminal for the developer watching it now, and the
        // file so a session can still be read back afterwards.
        using var fileLog = new FileLog();
        void Log(string message)
        {
            Console.WriteLine(message);
            fileLog.Write(message);
        }

        if (fileLog.Path is not null) Console.WriteLine($"Logging to {fileLog.Path}");

        try
        {
            await RunAsync(Log, cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Fatal error: {ex}");
            Console.Error.WriteLine($"Fatal error: {ex}");
            return 1;
        }
    }

    /// <summary>The main body, shared by service mode and console mode.</summary>
    internal static async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        var config = ServiceConfig.Load();
        log($"Configuration loaded. Default game: {config.DefaultGameId}, adapter: {config.AdapterName}");

        await using var engine = new TunnelEngine(config, log);
        var pipe = new PipeServer(engine, log);

        log($"Listening on pipe \\\\.\\pipe\\{Core.Ipc.IpcConstants.PipeName}");
        await pipe.RunAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class BoosterService : ServiceBase
{
    private readonly CancellationTokenSource _cts = new();
    private readonly FileLog _log = new();
    private Task? _worker;

    public BoosterService()
    {
        ServiceName = "GamePingBooster";
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _worker = Task.Run(async () =>
        {
            try
            {
                await Program.RunAsync(_log.Sink, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                // The full exception, not just the message: this is the only account of a crash
                // that anyone will ever get, and it has to survive the process exiting below.
                _log.Write($"Service stopped with an error: {ex}");
                _log.Dispose();
                // Let the SCM restart us according to the recovery settings.
                Environment.Exit(1);
            }
        });
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _cts.Cancel();
        // Give the engine time to remove routes and delete the adapter. If it overruns, the
        // adapter dies with the process and Windows cleans up the routes anyway.
        try { _worker?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The log goes last so it can record the shutdown it is about to stop recording.
            _cts.Dispose();
            _log.Dispose();
        }
        base.Dispose(disposing);
    }
}
