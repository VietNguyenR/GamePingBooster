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
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
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

        try
        {
            await RunAsync(Console.WriteLine, cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
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
                await Program.RunAsync(WriteEventLog, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                WriteEventLog($"Service stopped with an error: {ex}");
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

    private void WriteEventLog(string message)
    {
        try
        {
            EventLog.WriteEntry(message);
        }
        catch (Exception)
        {
            // Event log full or blocked - nothing to do, and this must never throw.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
