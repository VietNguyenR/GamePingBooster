using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Service.Discovery;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Network;

/// <summary>
/// The service's half of item 3 of the PC tune-up panel, "Ứng dụng dùng mạng" (pc-checklist-handoff.md): which apps
/// are downloading or uploading while the game runs, and pausing Windows Update for the length of the game.
///
/// Counting is never left running. The app watches the adapter's byte counters (free) and asks here only when
/// background traffic is over its bar while a game runs; each ask counts TCP bytes per process for
/// <see cref="Window"/> and stops (<see cref="TcpTraffic"/>). UDP is not named: what the big background downloaders
/// use - Windows Update, Steam, Epic, OneDrive, browsers' downloads - is TCP, and the app shows whatever is left as
/// unnamed.
///
/// The pause is the one change here, and it keeps the panel's rules: it lasts while a game runs and undoes itself when
/// the last one closes (checked every <see cref="WatchEvery"/>, connected or not - a Disconnect mid-match keeps updates
/// off till the game closes), on "Khôi phục", and when the service stops. What it
/// changed is written down before it changes anything (<see cref="MarkerPath"/>), so a crash or a power cut is undone
/// by the next start (<see cref="RestoreLeftovers"/>).
///
/// What a pause does: wuauserv (Windows Update) is set to Disabled and stopped, and DoSvc (Delivery Optimization, which
/// carries the downloads) is stopped. Windows fights both, so the watch holds them: DoSvc's configuration is protected -
/// Disabled is refused even to LocalSystem ("Access is denied", 2026-10-17) - and it is trigger-started; and when DoSvc
/// started again, wuauserv was back on Manual and running within 7 s (the update medic). Each watch tick stops whichever
/// is running and sets wuauserv to Disabled again.
/// Start types are put back exactly and the services that were running are started again.
/// </summary>
internal sealed class NetworkApps : IDisposable
{
    /// <summary>How long one count runs. Long enough for a download's rate to mean something, short enough to ask again.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(5);

    /// <summary>Below this an app is not worth a line (MB/s, in and out together).</summary>
    private const double MinMbps = 0.05;

    private const int MaxApps = 8;

    /// <summary>The services a pause stops, in the order they are stopped.</summary>
    private static readonly string[] PausedServices = ["wuauserv", "DoSvc"];

    /// <summary>The one whose start type Windows will not let anyone change: held stopped by the watch instead.</summary>
    private const string Protected = "DoSvc";

    /// <summary>A Service Host running any of these is "Windows Update" to the player.</summary>
    private static readonly HashSet<string> UpdateServices = new(["wuauserv", "DoSvc", "BITS", "UsoSvc", "WaaSMedicSvc"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Never named: the game's anti-cheat (the game's own names come from the profile) and this app.</summary>
    private static readonly HashSet<string> NeverNamed = new(
        ["BEService", "BEService_x64", "EasyAntiCheat", "EasyAntiCheat_EOS", "vgc", "vgtray", "ACE-Tray", "SGuard64",
         "SGuardSvc64", "GamePingBooster", "gpb-service", "gpb-etwwatch", "gpb-updater", "System", "Idle"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SteamNames = new(["steam", "steamwebhelper", "steamservice"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> EpicNames = new(["EpicGamesLauncher", "EpicWebHelper", "EpicOnlineServices"],
        StringComparer.OrdinalIgnoreCase);

    private static string MarkerPath => Path.Combine(ServiceConfig.DefaultDirectory, "updates-paused");

    private readonly Func<IEnumerable<string>> _gameProcessNames;
    private readonly Func<bool> _gameRunning;
    private readonly Func<IPAddress?> _tunnelAddress;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _counting = new(1, 1);
    private readonly object _gate = new();
    private Timer? _watch;
    private volatile bool _paused;
    private bool _disposed;

    public NetworkApps(Func<IEnumerable<string>> gameProcessNames, Func<bool> gameRunning, Func<IPAddress?> tunnelAddress,
        Action<string> log)
    {
        _gameProcessNames = gameProcessNames;
        _gameRunning = gameRunning;
        _tunnelAddress = tunnelAddress;
        _log = log;
    }

    /// <summary>Windows Update is paused by this service right now.</summary>
    public bool UpdatesPaused => _paused;

    /// <summary>Raised when the pause starts or ends - also by itself, when the game closes.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------ counting

    /// <summary>
    /// Counts TCP bytes per app for <see cref="Window"/>. Returns the apps busiest first, or an error when a count is
    /// already running (the app asks again) or nothing could be counted.
    /// </summary>
    public async Task<(List<NetAppUsage>? Apps, string? Error)> CountAsync(CancellationToken ct)
    {
        if (!await _counting.WaitAsync(0, ct).ConfigureAwait(false)) return (null, "A count is already running.");
        try
        {
            return await Task.Run(() => CountCoreAsync(ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _counting.Release();
        }
    }

    private async Task<(List<NetAppUsage>? Apps, string? Error)> CountCoreAsync(CancellationToken ct)
    {
        var self = (uint)Environment.ProcessId;
        var skip = new HashSet<string>(NeverNamed, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _gameProcessNames())
            skip.Add(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);

        var names = new Dictionary<uint, string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p) names[(uint)p.Id] = p.ProcessName;
        }

        // Connections riding the tunnel are the game's routes, and the app takes the tunnel's bytes off the line's
        // figure already; LAN traffic does not use the internet line at all.
        var inner = _tunnelAddress();
        var conns = TcpTraffic.Established()
            .Where(c => c.Pid != self && c.Pid > 4 && !Destinations.IsLocal(c.Remote)
                        && (inner is null || !c.Local.Equals(inner))
                        && names.TryGetValue(c.Pid, out var n) && !skip.Contains(n))
            .ToList();

        var counted = new List<(TcpTraffic.Conn Conn, TcpTraffic.Counters First)>();
        var refused = 0;
        foreach (var conn in conns)
        {
            if (TcpTraffic.SetCounting(conn, true) != 0)
            {
                refused++;
                continue;
            }
            if (TcpTraffic.Read(conn) is { } first) counted.Add((conn, first));
        }
        if (counted.Count == 0 && refused > 0) return (null, $"Windows would not count TCP bytes ({refused} connections).");

        var started = Stopwatch.GetTimestamp();
        var byPid = new Dictionary<uint, (double In, double Out)>();
        double seconds;
        try
        {
            await Task.Delay(Window, ct).ConfigureAwait(false);
        }
        finally
        {
            // Read and switched off together, even when cancelled: collection left on costs every segment a little.
            seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            foreach (var (conn, first) in counted)
            {
                if (TcpTraffic.Read(conn) is { } last && last.BytesIn >= first.BytesIn && last.BytesOut >= first.BytesOut)
                {
                    var sum = byPid.GetValueOrDefault(conn.Pid);
                    byPid[conn.Pid] = (sum.In + (last.BytesIn - first.BytesIn), sum.Out + (last.BytesOut - first.BytesOut));
                }
                TcpTraffic.SetCounting(conn, false);
            }
        }
        return (Name(byPid, seconds, names), null);
    }

    /// <summary>Folds processes into what a player recognises - "Windows Update", "Steam" - busiest first.</summary>
    private static List<NetAppUsage> Name(Dictionary<uint, (double In, double Out)> byPid, double seconds,
        Dictionary<uint, string> names)
    {
        const double Mb = 1024 * 1024;
        var busy = byPid.Where(p => (p.Value.In + p.Value.Out) / Mb / seconds >= MinMbps / 4).ToList();
        Dictionary<uint, List<ServiceHosts.Hosted>>? services = null;

        var apps = new Dictionary<string, NetAppUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, (bytesIn, bytesOut)) in busy)
        {
            var process = names.GetValueOrDefault(pid) ?? "";
            string key, name;
            if (process.Equals("svchost", StringComparison.OrdinalIgnoreCase))
            {
                services ??= ServiceHosts.ByPid();
                var hosted = services.GetValueOrDefault(pid) ?? [];
                if (hosted.Any(s => UpdateServices.Contains(s.Name)))
                {
                    (key, name) = ("windows-update", "Windows Update");
                }
                else
                {
                    var first = hosted.Count > 0 ? hosted[0] : new ServiceHosts.Hosted("svchost", "Service Host");
                    (key, name) = ("svc-" + first.Name, first.DisplayName);
                }
            }
            else if (SteamNames.Contains(process))
            {
                (key, name) = ("steam", "Steam");
            }
            else if (EpicNames.Contains(process))
            {
                (key, name) = ("epic", "Epic Games Launcher");
            }
            else
            {
                (key, name) = (process, Describe(pid, process));
            }

            if (!apps.TryGetValue(key, out var app)) apps[key] = app = new NetAppUsage { Key = key, Name = name };
            app.DownMbps += bytesIn / Mb / seconds;
            app.UpMbps += bytesOut / Mb / seconds;
        }
        return [.. apps.Values
            .Where(a => a.DownMbps + a.UpMbps >= MinMbps)
            .OrderByDescending(a => a.DownMbps + a.UpMbps)
            .Take(MaxApps)
            .Select(a => { a.DownMbps = Math.Round(a.DownMbps, 2); a.UpMbps = Math.Round(a.UpMbps, 2); return a; })];
    }

    /// <summary>The exe's description as Task Manager shows it ("Microsoft OneDrive"), or the process name.</summary>
    private static string Describe(uint pid, string process)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var path = p.MainModule?.FileName;
            if (path is not null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                return description.Trim();
        }
        catch (Exception)
        {
            // Protected, or gone already.
        }
        return process;
    }

    // ------------------------------------------------------------ pausing Windows Update

    private sealed record Paused(string Name, uint StartType, bool WasRunning);

    /// <summary>Pauses Windows Update until the game closes. Null when paused (or already), else why not.</summary>
    public string? PauseUpdates()
    {
        lock (_gate)
        {
            if (_disposed) return "The service is stopping.";
            if (_paused) return null;
            if (!_gameRunning()) return "No game is running - updates are paused only while one is.";

            var entries = new List<Paused>();
            foreach (var name in PausedServices)
            {
                try
                {
                    using var sc = new ServiceController(name);
                    entries.Add(new Paused(name, (uint)sc.StartType, sc.Status == ServiceControllerStatus.Running));
                }
                catch (Exception ex)
                {
                    _log($"Updates: could not read the service {name} ({ex.Message}).");
                }
            }
            if (entries.Count == 0) return "The Windows Update services were not found.";

            // Written first: whatever happens next, the next start knows what to put back.
            try
            {
                AtomicFile.WriteAllText(MarkerPath,
                    string.Join('\n', entries.Select(e => $"{e.Name}\t{e.StartType}\t{(e.WasRunning ? 1 : 0)}")));
            }
            catch (Exception ex)
            {
                return $"Could not write {MarkerPath} ({ex.Message}) - nothing was changed.";
            }

            var held = 0;
            foreach (var e in entries)
            {
                if (!e.Name.Equals(Protected, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ServiceHosts.SetStartType(e.Name, (uint)ServiceStartMode.Disabled);
                        held++;
                    }
                    catch (Exception ex)
                    {
                        _log($"Updates: Windows would not set {e.Name} to Disabled ({ex.Message}).");
                    }
                }
                if (e.WasRunning) StopService(e.Name);
            }

            if (held == 0)
            {
                RestoreLocked(entries, start: true);
                return "Windows would not pause the update services.";
            }

            _paused = true;
            _restops = 0;
            _watch = new Timer(_ => WatchGame(), null, WatchEvery, WatchEvery);
            _log("Updates: Windows Update paused until the game closes (" +
                 string.Join(", ", entries.Select(e => $"{e.Name} was {(ServiceStartMode)e.StartType}{(e.WasRunning ? ", running" : "")}")) + ").");
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>Puts Windows Update back as it was. Safe when nothing is paused.</summary>
    public void ResumeUpdates(string why)
    {
        lock (_gate)
        {
            if (!_paused) return;
            _watch?.Dispose();
            _watch = null;
            RestoreLocked(ReadMarker(), start: true);
            _paused = false;
            _log($"Updates: Windows Update resumed ({why}).");
        }
        Changed?.Invoke();
    }

    private void WatchGame()
    {
        try
        {
            if (!_gameRunning())
            {
                ResumeUpdates("the game closed");
                return;
            }
            lock (_gate)
            {
                if (!_paused) return;
                foreach (var name in PausedServices)
                {
                    using var sc = new ServiceController(name);
                    var reEnabled = !name.Equals(Protected, StringComparison.OrdinalIgnoreCase)
                                    && sc.StartType != ServiceStartMode.Disabled;
                    var running = sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending);
                    if (!reEnabled && !running) continue;
                    _restops++;
                    if (_restops <= 5)
                        _log($"Updates: Windows {(reEnabled ? $"set {name} back to {sc.StartType}" : $"started {name} again")} " +
                             "during the pause - held it again.");
                    if (reEnabled)
                    {
                        try { ServiceHosts.SetStartType(name, (uint)ServiceStartMode.Disabled); }
                        catch (Exception ex) { _log($"Updates: could not set {name} to Disabled again ({ex.Message})."); }
                    }
                    if (running) StopService(name);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Updates: could not check the game ({ex.Message}).");
        }
    }

    /// <summary>How often the watch had to hold a service again during this pause; logged the first few times only.</summary>
    private int _restops;

    private void StopService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status != ServiceControllerStatus.Stopped) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            _log($"Updates: could not stop {name} ({ex.Message}).");
        }
    }

    private void RestoreLocked(List<Paused> entries, bool start)
    {
        foreach (var e in entries.Where(e => !e.Name.Equals(Protected, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                ServiceHosts.SetStartType(e.Name, e.StartType);
            }
            catch (Exception ex)
            {
                _log($"Updates: could not put {e.Name} back to {(ServiceStartMode)e.StartType} ({ex.Message}).");
            }
        }
        if (start)
        {
            foreach (var e in entries.Where(e => e.WasRunning))
            {
                try
                {
                    using var sc = new ServiceController(e.Name);
                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                }
                catch (Exception ex)
                {
                    _log($"Updates: could not start {e.Name} again ({ex.Message}) - Windows starts it when it needs it.");
                }
            }
        }
        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception ex)
        {
            _log($"Updates: could not delete {MarkerPath} ({ex.Message}).");
        }
    }

    private static List<Paused> ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return [];
            return [.. File.ReadAllLines(MarkerPath)
                .Select(line => line.Split('\t'))
                .Where(f => f.Length == 3 && PausedServices.Contains(f[0], StringComparer.OrdinalIgnoreCase)
                            && uint.TryParse(f[1], out var t) && t is >= 2 and <= 4)
                .Select(f => new Paused(f[0], uint.Parse(f[1]), f[2] == "1"))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// At service start: a pause the last run never undid (killed, crashed, power cut) is undone now - start types put
    /// back. The services are not started: Windows starts them when it needs them, and the game may be running.
    /// </summary>
    public static void RestoreLeftovers(Action<string> log)
    {
        var entries = ReadMarker();
        if (entries.Count == 0)
        {
            try { File.Delete(MarkerPath); } catch (Exception) { }
            return;
        }
        var apps = new NetworkApps(() => [], () => false, () => null, log);
        lock (apps._gate) apps.RestoreLocked(entries, start: false);
        log("Updates: a Windows Update pause left by the last run was undone (" +
            string.Join(", ", entries.Select(e => $"{e.Name} -> {(ServiceStartMode)e.StartType}")) + ").");
    }

    public void Dispose()
    {
        ResumeUpdates("the service is stopping");
        lock (_gate) _disposed = true;
        _counting.Dispose();
    }
}
