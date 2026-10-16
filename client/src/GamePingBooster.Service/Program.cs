using System.ServiceProcess;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Service.Dns;
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
///
/// And one for support and development, which prints this machine's device public key:
///
///   gpb-service.exe --device-key
///
/// And one that exercises the name resolver and prints what it found, installing nothing:
///
///   gpb-service.exe --dns-selftest [--policy]
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

        // Prints the device public key and nothing else, so it can be piped somewhere. It has
        // the side effect of CREATING the identity if there is not one yet, which is the same
        // thing starting the service does - there is no separate "generate" step to forget.
        //
        // Run it as SYSTEM (psexec -s) to see what the service sees. Run as a normal user it
        // still works, because DPAPI here is machine scope, but %ProgramData% may not be
        // writable, in which case it reports a key that will not survive.
        if (args.Contains("--device-key", StringComparer.OrdinalIgnoreCase))
        {
            using var device = DeviceIdentity.LoadOrCreate(Console.Error.WriteLine);
            Console.WriteLine(device.PublicKeyHex);
            return 0;
        }

        // Exercises the Steam resolver without installing anything. See DnsSelfTest: the half that
        // needs no rights is the half a developer can run, and --policy adds the half that only
        // SYSTEM can write.
        if (args.Contains("--dns-selftest", StringComparer.OrdinalIgnoreCase))
        {
            var withPolicy = args.Contains("--policy", StringComparer.OrdinalIgnoreCase);
            return Dns.DnsSelfTest.RunAsync(withPolicy, CancellationToken.None).GetAwaiter().GetResult();
        }

        // What the AutoUpdater would decide, installing nothing: the release it would take for a given version, and
        // whether a setup .exe passes its signature check. For support ("why does this PC not update") and for
        // checking a new signing certificate before a release depends on it.
        //
        //   gpb-service.exe --update-check [version] [setup.exe]
        var updateCheck = Array.FindIndex(args, a => a.Equals("--update-check", StringComparison.OrdinalIgnoreCase));
        if (updateCheck >= 0)
        {
            var version = args.Length > updateCheck + 1 ? args[updateCheck + 1] : Update.AutoUpdater.CurrentVersion();
            var cache = Path.Combine(Path.GetTempPath(), "gpb-update-check.json");
            var update = Core.Updates.ReleaseFeed.CheckAsync(version, cache, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(update is null
                ? $"Nothing newer than v{version}."
                : $"v{update.Version} is newer than v{version}; installer {update.InstallerUrl ?? "(not found)"} sha256 {update.InstallerSha256 ?? "-"}");
            if (args.Length > updateCheck + 2)
            {
                var problem = Update.Authenticode.Check(Path.GetFullPath(args[updateCheck + 2]));
                Console.WriteLine(problem is null ? "Signature: trusted." : $"Signature: refused - {problem}.");
                return problem is null ? 0 : 1;
            }
            return 0;
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
#if DEBUG
        // A developer's build shows the terminal in the clear - `./gpb dev`. The file is sealed either way.
        void Log(string message)
        {
            Console.WriteLine(message);
            fileLog.Write(message);
        }
#else
        // A release build can be run with --console by anyone with admin rights, so the terminal is sealed like the
        // file (LogSeal) - otherwise --console would be the way round it.
        var consoleSeal = new LogSeal();
        Console.WriteLine(consoleSeal.KeyLine);
        void Log(string message)
        {
            Console.WriteLine(consoleSeal.Seal(message));
            if (consoleSeal.KeyLineDue()) Console.WriteLine(consoleSeal.KeyLine);
            fileLog.Write(message);
        }
#endif

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
        if (ServiceConfig.LoadProblem is { } loadProblem) log(loadProblem);
        log($"Configuration loaded. Default game: {config.DefaultGameId}, adapter: {config.AdapterName}");

        await using var engine = new TunnelEngine(config, log);

        // Releases are installed by the service itself, silently, while nothing is connected or playing - see
        // AutoUpdater. Created before the rest of startup so the attempt the previous service started is closed
        // (and the app it closed started again) whatever happens after; its first check waits 20 s.
        PipeServer? pipe = null;
        await using var updater = new Update.AutoUpdater(
            enabled: config.AutoUpdate ?? true,
            tunnelState: () => engine.State,
            gameRunning: engine.AnyGameRunning,
            uiSession: () => pipe?.UiSession,
            log);
        engine.StatusChanged += status => updater.NoteTunnel(status.State);
        updater.Start();

        // Before anything else decides anything: a name resolution policy is machine-wide and
        // survives this process. If the last run was killed rather than stopped, its rules are
        // still pointing the claimed names at a resolver that is no longer listening - which breaks
        // those sites in a way that survives a reboot and has no visible cause. The only place that
        // is guaranteed to run after a crash is the next start, so it is cleaned up here.
        UnblockDns.RemoveLeftovers(log);

        // The policy is read through the engine because that is where the delivered profile lands,
        // and read on every attempt rather than captured once: the profile arrives after startup,
        // so a list captured here would be the built-in fallback for the life of the process.
        // The engine also stands in as the tunnel for the few names the profile routes through it (UnblockApp.tunnel):
        // a line that resets a handshake by its name is past what DNS can fix.
        await using var unblock = new UnblockDns(() => engine.UnblockPolicy, log, engine);
        engine.LobbyConnections = unblock.DescribeLobbyConnections;

        // The game's TCP connections - lobby, store, images - per name in the log, while it runs. Logs only.
        engine.NameOfConnection = unblock.NameOfConnection;
        var lobbyWatch = Task.Run(() => engine.RunLobbyWatchAsync(ct), CancellationToken.None);

        // When unblocking goes wrong, the line is checked the way tools\Check-Unblock.ps1 checks it and the result
        // sent to /admin/unblock/reports - under the same Settings switch as connection quality and discovery.
        using var unblockReports = engine.CreateUnblockReporter(
            () => (unblock.Enabled, unblock.LastError),
            Path.Combine(ServiceConfig.DefaultDirectory, "logs", "gpb-service.log"),
            log);
        unblock.Trouble = unblockReports.Trouble;
        engine.AfterMatchTrouble = unblockReports.Trouble;

        // When it runs. A self-hosted installation - no licence server - unblocks for as long as the service runs,
        // as every installation did until 2026-10-02. A licensed one unblocks while it is CONNECTED, by the owner's
        // decision that day: running with the service meant installing the app once, never paying, and keeping the
        // unblocking for good, from the profile left on disk or the Steam list that used to be compiled in. Connect
        // needs a licence the relay accepts, so tying unblocking to it ties it to a paid or trial account.
        //
        // On at Connected; off at Disconnected (Disconnect, or the app closing) and Faulted (gave up). Connecting and
        // Reconnecting change nothing, so a failover or a move between relays mid-match does not take the fix away.
        // The cost, accepted: reading the Steam store needs Connect now (it did not from 2026-09-22).
        if (!(config.UnblockEnabled ?? true))
        {
            log("Name unblocking is switched off in config.json.");
        }
        else if (string.IsNullOrWhiteSpace(config.LicenceUrl))
        {
            unblock.Start(ct);
        }
        else
        {
            log("Name unblocking runs while connected.");
            var unblockOn = false;
            var unblockGate = new object();
            engine.StatusChanged += message =>
            {
                bool? want = message.State switch
                {
                    TunnelState.Connected => true,
                    TunnelState.Disconnected or TunnelState.Faulted => false,
                    _ => null,
                };
                if (want is not { } on) return;
                lock (unblockGate)
                {
                    if (on == unblockOn) return;
                    unblockOn = on;
                }
                if (on)
                {
                    unblock.Start(ct);
                }
                else
                {
                    _ = Task.Run(() => unblock.StopAsync(
                        message.State == TunnelState.Faulted
                            ? "Off - the connection failed. Unblocking runs while connected."
                            : "Off - unblocking runs while connected. Press Connect to turn it on."));
                }
            };
        }

        // Load the profile now, not at the first connect.
        //
        // Everything the UI asks about configuration goes through Snapshot, and Snapshot answers
        // "is this installation configured" partly from the relay list - which lives in the
        // profile. Loading it lazily meant a perfectly configured machine reported itself
        // unconfigured until somebody pressed Connect: the first-run banner appeared on every
        // start, and the settings screen decided there was no stored key, so it demanded the
        // pre-shared key again and refused to save without it. One missing call, three symptoms,
        // none of which pointed at it.
        //
        // Best effort on purpose. A missing or unreadable profile is not a reason to refuse to
        // start - the same reasoning as ServiceConfig.Load no longer throwing.
        try
        {
            await engine.LoadProfileAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"Could not load the profile at startup ({ex.Message}). " +
                "It will be tried again on the next connect.");
        }

        pipe = new PipeServer(engine, unblock, updater, log);

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
