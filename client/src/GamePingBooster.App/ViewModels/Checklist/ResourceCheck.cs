using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;
using static GamePingBooster.App.ViewModels.Checklist.ResourceRules;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item 4: CPU, GPU, RAM and disk (pc-checklist-handoff.md; the owner's addition - background load causing ping and
/// stutter has been seen live). Nothing to press yet: closing a process for the player is a later step, and never the
/// game.
///
/// The question is not "is the PC busy" but "is something else taking what the game needs". So while a game runs, its
/// own share is measured and only the rest - background apps - counts against the line, and how much background is
/// too much depends on how heavy the game is ON THIS PC: PUBG at 30 % CPU on a laptop leaves no room for a 10 %
/// background app, Valorant at 8 % on a 14700K does. No per-game list to keep up to date, and the PC is part of the
/// answer. Before a game runs, everything counts as background and the bars are about leaving room for one.
///
/// Disk is judged on latency (how long an answer takes), RAM on free MB - see <see cref="PcResources"/> for why.
///
/// Why it is here: 23 % of in-match spikes over the 7 days to 2026-10-16 had the "pc" verdict, and a CPU-bound PC is
/// also what starved the keepalive in mid-match reconnects (mid-match-reconnect-is-keepalive-starvation).
/// </summary>
public sealed class ResourceCheck : CheckItem
{
    private static readonly Geometry ChipIcon = Geometry.Parse(
        "M4,4 H12 V12 H4 Z M6.5,6.5 H9.5 V9.5 H6.5 Z M6,1 V4 M10,1 V4 M6,12 V15 M10,12 V15 M1,6 H4 M1,10 H4 M12,6 H15 M12,10 H15");

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private const int Window = 5; // 5 x 2 s = 10 s

    /// <summary>Anti-cheat runs beside the game and is counted on the game's side, never named as background.</summary>
    private static readonly string[] AntiCheat =
        ["BEService", "BEService_x64", "EasyAntiCheat", "EasyAntiCheat_EOS", "vgc", "vgtray", "ACE-Tray", "SGuard64", "SGuardSvc64"];

    /// <summary>
    /// Nothing the player can close while playing, so neither background nor ever named:
    /// - Windows itself. dwm draws every window, the game's frames included - on 2026-10-17 it was named at 9 % GPU
    ///   beside PUBG (owner: "dwm is Windows, it cannot be closed"). Services and exes under the Windows folder are
    ///   found without a list (<see cref="PcResources.IsWindowsPart"/>); these names are the ones that check misses
    ///   (the session-1 csrss/winlogon/dwm get no handle) or that run from elsewhere.
    /// - The game's own launcher (Steam, Epic, Riot...): the game does not run without it. A named app stays listed
    ///   until it is closed, so naming one would be a warning the player can never clear.
    /// - This app.
    /// </summary>
    private static readonly HashSet<string> WindowsCore = new(
        ["dwm", "csrss", "Registry", "Memory Compression", "smss", "wininit", "winlogon", "services", "lsass",
         "fontdrvhost", "audiodg",
         "steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "EpicWebHelper", "RiotClientServices",
         "RiotClientUx", "RiotClientUxRender", "Battle.net", "EADesktop", "EABackgroundService",
         "GamePingBooster", "gpb-service", "gpb-etwwatch", "gpb-updater"], StringComparer.OrdinalIgnoreCase);

    public override string Id => "resources";
    public override Geometry Icon => ChipIcon;
    public override string Title => T("check.res.title");

    private readonly PipeClient _pipe;
    private volatile Reading? _reading;
    private volatile HashSet<string> _gameProcesses = new(AntiCheat, StringComparer.OrdinalIgnoreCase);
    private volatile bool _gameRunning;
    private volatile bool _namesKnown;
    private volatile bool _watchNamed;

    /// <summary>
    /// Apps the player chose to skip (<see cref="IgnoredAppsStore"/>), process name to shown name: counted with Windows'
    /// own share, never named. Replaced whole on a change, so the measuring thread reads a stable set. UI thread writes.
    /// </summary>
    private volatile Dictionary<string, string> _ignored = IgnoredAppsStore.Load();
    private string? _askedFor;
    private CancellationTokenSource? _loop;

    public ResourceCheck(PipeClient pipe) => _pipe = pipe;

    public override void OnStatus(StatusMessage status)
    {
        // The game's process names come only in the "games" reply, never in the heartbeat. Without them TslGame
        // counted as background: "game 0%" and the game itself named as the app to close (owner, 2026-10-17).
        if (status.AckVerb == "games" && status.Games is { } games)
        {
            var names = new HashSet<string>(AntiCheat, StringComparer.OrdinalIgnoreCase);
            foreach (var name in games.SelectMany(g => g.ProcessNames))
                names.Add(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
            _gameProcesses = names;
            _namesKnown = true;
        }
        _gameRunning = status.GameRunning;

        // Asked once per game that opens (and again if the answer never came), not every second.
        var running = status.GameRunning ? status.GameName ?? "" : null;
        if (running is not null && (running != _askedFor || !_namesKnown))
        {
            if (running != _askedFor) _namesKnown = false;
            _askedFor = running;
            _ = AskGamesAsync();
        }
        else if (running is null)
        {
            _askedFor = null;
        }

        if (status.State == TunnelState.Connected)
        {
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                var token = _loop.Token;
                _ = Task.Run(() => MeasureAsync(token));
            }
        }
        else if (status.State is TunnelState.Disconnected or TunnelState.Faulted)
        {
            Stop();
        }
        Evaluate();
    }

    public override void OnServiceGone()
    {
        Stop();
        Evaluate();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Evaluate();
    }

    private async Task AskGamesAsync()
    {
        try
        {
            await _pipe.SendAsync(new CommandMessage { Verb = "games" }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The service is not reachable; the next heartbeat with the game running asks again.
            Dispatcher.UIThread.Post(() => _askedFor = null);
        }
    }

    /// <summary>Stops measuring. The last reading stays on screen.</summary>
    private void Stop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    // ------------------------------------------------------------ measuring (thread pool)

    private sealed class Tick
    {
        public double Cpu, GameCpu, GameGpu, WindowsCpu, WindowsGpu, DiskMs, BackgroundDiskMb;
        public double? Gpu;
    }

    private async Task MeasureAsync(CancellationToken token)
    {
        using var resources = new PcResources();
        var ticks = new Queue<Tick>();
        PcResources.ProcessSnapshot? previous = null;
        var wasPlaying = false;

        resources.Read(); // starts the CPU interval
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var load = resources.Read();
                // A game is up but which processes are its own is not known yet (the "games" reply is on its way):
                // judging now would name the game as background. Wait a tick.
                if (_gameRunning && !_namesKnown)
                {
                    previous = null;
                    ticks.Clear();
                    continue;
                }
                var games = _gameProcesses;
                var tick = new Tick { Cpu = load.CpuPercent, Gpu = load.Gpu?.TotalPercent, DiskMs = load.Disk?.LatencyMs ?? 0 };

                // The per-process scan runs every tick while a game is up (the game's own share is the whole point) or
                // while an app is listed (to see it closed), otherwise only while something already looks high.
                var looksHigh = load.CpuPercent >= IdleWarn || load.Gpu?.TotalPercent >= IdleGpuWarn
                                || load.Disk?.LatencyMs >= DiskWarnMs || load.RamAvailableMb < RamWarnIdle;
                List<PcResources.Usage>? usage = null;
                if (_gameRunning || looksHigh || _watchNamed)
                {
                    var now = PcResources.TakeSnapshot();
                    if (previous is not null) usage = PcResources.Between(previous, now);
                    previous = now;
                }
                else
                {
                    previous = null;
                }

                var playing = false;
                List<TopProcess> topCpu = [], topGpu = [], topDisk = [], topRam = [];
                HashSet<string>? running = null, windowed = null;
                if (usage is not null)
                {
                    var game = usage.Where(u => games.Contains(u.Name)).ToList();
                    // Windows' own share: the fixed names plus every service and every exe under the Windows folder.
                    // Apps the player chose to skip go with it: not background, never named.
                    var ignored = _ignored;
                    var windows = usage.Where(u => !games.Contains(u.Name)
                                                   && (WindowsCore.Contains(u.Name) || ignored.ContainsKey(u.Name)
                                                       || PcResources.IsWindowsPart(u))).ToList();
                    var windowsPids = windows.Select(u => u.Pid).ToHashSet();
                    var background = usage.Where(u => !games.Contains(u.Name) && !windowsPids.Contains(u.Pid)).ToList();
                    // Anti-cheat alone (BEService stays up after a match) is not the game running.
                    playing = _gameRunning && game.Any(u => !AntiCheat.Contains(u.Name, StringComparer.OrdinalIgnoreCase));

                    tick.GameCpu = game.Sum(u => u.CpuPercent);
                    tick.WindowsCpu = windows.Sum(u => u.CpuPercent);
                    tick.BackgroundDiskMb = background.Sum(u => u.IoMbPerSecond);
                    topCpu = PcResources.Top(background, u => u.Name, u => u.CpuPercent);
                    topDisk = PcResources.Top(background, u => u.Name, u => u.IoMbPerSecond);
                    topRam = PcResources.Top(background, u => u.Name, u => u.RamMb);

                    if (load.Gpu is { } gpu)
                    {
                        var names = usage.ToDictionary(u => u.Pid, u => u.Name);
                        var perPid = gpu.ByPid
                            .Select(p => (Name: names.GetValueOrDefault(p.Key) ?? "", Gpu: p.Value, Windows: windowsPids.Contains(p.Key)))
                            .ToList();
                        tick.GameGpu = perPid.Where(p => games.Contains(p.Name)).Sum(p => p.Gpu);
                        tick.WindowsGpu = perPid.Where(p => p.Windows).Sum(p => p.Gpu);
                        topGpu = PcResources.Top(
                            perPid.Where(p => p.Name.Length > 0 && !games.Contains(p.Name) && !p.Windows),
                            p => p.Name, p => p.Gpu);
                    }

                    // "chrome" means nothing to most players: name it as Task Manager does ("Google Chrome").
                    running = usage.Select(u => u.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var withWindow = PcResources.PidsWithWindow();
                    windowed = usage.Where(u => withWindow.Contains(u.Pid)).Select(u => u.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var pids = usage.GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Pid, StringComparer.OrdinalIgnoreCase);
                    (topCpu, topGpu, topDisk, topRam) = (PcResources.Describe(topCpu, pids), PcResources.Describe(topGpu, pids),
                        PcResources.Describe(topDisk, pids), PcResources.Describe(topRam, pids));
                }
                if (!playing) tick.GameCpu = tick.GameGpu = tick.WindowsCpu = tick.WindowsGpu = 0;

                // The game just opened or closed: the 10 s average would mix seconds judged by the other bars (the
                // launcher's work before the game counted as background beside a "game 1%").
                if (usage is not null && playing != wasPlaying)
                {
                    ticks.Clear();
                    wasPlaying = playing;
                }
                ticks.Enqueue(tick);
                while (ticks.Count > Window) ticks.Dequeue();

                var gpus = ticks.Where(t => t.Gpu is not null).Select(t => t.Gpu!.Value).ToList();
                Publish(new Reading(
                    playing,
                    ticks.Average(t => t.Cpu), ticks.Average(t => t.GameCpu), ticks.Average(t => t.WindowsCpu),
                    gpus.Count > 0 ? gpus.Average() : null, ticks.Average(t => t.GameGpu), ticks.Average(t => t.WindowsGpu),
                    ticks.Average(t => t.DiskMs), ticks.Average(t => t.BackgroundDiskMb),
                    load.RamAvailableMb,
                    topCpu, topGpu, topDisk, topRam, running, windowed));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Publish(Reading reading)
    {
        _reading = reading;
        Dispatcher.UIThread.Post(Evaluate);
    }

    // ------------------------------------------------------------ what the line says (UI thread)

    /// <summary>
    /// The background apps named so far per resource (CPU, GPU, RAM, disk, in verdict order), by process name, at the
    /// most each was seen using. Owner, 2026-10-17: when the load is spread out the heaviest app changes every few
    /// seconds - "nhiều nhất: Cakewalk Sonar 3%" then "Docker Desktop 3%" - too fast to read; and an app that took
    /// what the game needed once will do it again, so its warning must not go green the moment it rests. So a name,
    /// once over the bar, stays listed - and keeps the line amber - until that app is closed. UI thread only.
    /// </summary>
    private readonly Dictionary<string, TopProcess>[] _named = [new(), new(), new(), new()];

    /// <summary>At most this many names per line: the list is advice, not Task Manager.</summary>
    private const int MaxNamed = 5;

    /// <summary>The line kept up by listed apps alone, once the load has dropped; same order as the verdicts.</summary>
    private static readonly string[] StickyKeys =
        ["check.res.cpuNamed", "check.res.gpuNamed", "check.res.ramNamed", "check.res.diskNamed"];

    private static readonly Func<double, string>[] Formats = [Pct, Pct, Size, Rate];

    private void Evaluate()
    {
        var r = _reading;
        if (r is null)
        {
            Severity = CheckSeverity.Unknown;
            Summary = _loop is null ? T("check.pending") : T("check.res.measuring");
            Detail = null;
            Lines = [];
            return;
        }

        var verdicts = new[] { JudgeCpu(r), JudgeGpu(r), JudgeRam(r), JudgeDisk(r) };
        var tops = new[] { r.TopCpu, r.TopGpu, r.TopRam, r.TopDisk };
        var games = _gameProcesses;
        for (var i = 0; i < verdicts.Length; i++)
        {
            var named = _named[i];
            if (verdicts[i].NameAtLeast is { } atLeast)
                foreach (var top in tops[i].Where(t => t.Amount >= atLeast))
                    if (!named.TryGetValue(top.Key, out var seen) || top.Amount > seen.Amount) named[top.Key] = top;
            // Gone: closed since (only a reading that scanned the processes can tell), or found to be the game's own
            // once its process names came in.
            foreach (var key in named.Keys.Where(k => r.Running?.Contains(k) == false || games.Contains(k)
                                                      || _ignored.ContainsKey(k)).ToList())
                named.Remove(key);
        }
        _watchNamed = _named.Any(n => n.Count > 0);

        var parts = new List<string> { "CPU " + Pct(r.Cpu) + Game(r, r.GameCpu) };
        if (r.Gpu is { } gpu) parts.Add("GPU " + Pct(gpu) + Game(r, r.GameGpu));
        parts.Add(Loc.F("check.res.ramFree", Size(r.RamFreeMb)));
        parts.Add(Loc.F("check.res.disk", Ms(r.DiskMs)));
        Summary = string.Join(" · ", parts);

        var severity = CheckSeverity.Good;
        var lines = new List<DetailLine>();
        bool background = false, gameItself = false;
        for (var i = 0; i < verdicts.Length; i++)
        {
            var v = verdicts[i];
            var named = _named[i].Values.OrderByDescending(t => t.Amount).Take(MaxNamed).ToList();
            if (v.Severity == CheckSeverity.Good && named.Count == 0) continue;

            // Load back to normal, but an app that took it is still open: amber until it is closed.
            var lineSeverity = v.Severity == CheckSeverity.Good ? CheckSeverity.Warning : v.Severity;
            if (lineSeverity < severity) severity = lineSeverity; // ordered worst first
            var line = v.Severity == CheckSeverity.Good ? T(StickyKeys[i]) : v.Line!;
            gameItself |= v.GameItself;

            // Nobody over the bar yet, the load spread thin: still say who is taking the most right now, without
            // keeping them listed - a warning with no name was unreadable (owner, 2026-10-17).
            var shown = named.Count > 0 || v.NameAtLeast is not { } bar
                ? named
                : tops[i].Where(t => t.Amount >= bar / 3 && !_ignored.ContainsKey(t.Key)).Take(3).ToList();
            if (shown.Count == 0)
            {
                lines.Add(new DetailLine(line));
                continue;
            }
            background = true;
            lines.Add(new DetailLine(v.Severity == CheckSeverity.Good ? line : line + T("check.res.shouldClose")));
            var format = Formats[i];
            lines.AddRange(shown.Select(t => AppLine(t, format, r.Windowed)));
        }
        Severity = severity;
        if (lines.Count > 0)
        {
            if (background || verdicts.Any(v => v.Severity != CheckSeverity.Good && !v.GameItself))
                lines.Add(new DetailLine(T("check.res.advice")));
            if (gameItself) lines.Add(new DetailLine(T("check.res.adviceGame")));
        }

        // What is being skipped stays in view, with the way back: a skipped app is still on the PC.
        if (_ignored.Count > 0)
        {
            lines.Add(new DetailLine(T("check.res.ignored")));
            lines.AddRange(_ignored.OrderBy(a => a.Value, StringComparer.CurrentCultureIgnoreCase).Select(a => new DetailLine(
                "• " + a.Value, IsEntry: true, [new LineButton(T("check.res.unignore"), () => Unignore(a.Key))])));
        }
        Detail = null;
        Lines = lines;
    }

    /// <summary>
    /// One named app. With a window, closing it is the player's to do and the line only offers "Bỏ qua". Without one it
    /// is still running with nothing to close - Cakewalk Sonar stayed in Task Manager's background processes after its
    /// window was closed, and a player would never look there (owner, 2026-10-17) - so the line says "(chạy ngầm)" and
    /// offers "Tắt hẳn", which asks once more ("Chắc chắn?") before ending it: unsaved work in it is lost.
    /// </summary>
    private DetailLine AppLine(TopProcess app, Func<double, string> format, IReadOnlySet<string>? windowed)
    {
        var text = "• " + app.Name + " " + format(app.Amount);
        var ignore = new LineButton(T("check.res.ignore"), () => Ignore(app));
        if (windowed is null || windowed.Contains(app.Key)) return new DetailLine(text, IsEntry: true, [ignore]);

        if (_endFailed.Contains(app.Key)) return new DetailLine(text + T("check.res.endFailed"), IsEntry: true, [ignore]);
        var end = _confirmEnd == app.Key
            ? new LineButton(T("check.res.endConfirm"), () => End(app.Key))
            : new LineButton(T("check.res.end"), () => AskEnd(app.Key));
        return new DetailLine(text + T("check.res.hidden"), IsEntry: true, [end, ignore]);
    }

    /// <summary>The app whose "Tắt hẳn" was pressed once and now reads "Chắc chắn?"; it goes back after 5 s.</summary>
    private string? _confirmEnd;
    private DispatcherTimer? _confirmTimer;

    /// <summary>Apps that could not be ended (started as administrator): the button is not offered again this run.</summary>
    private readonly HashSet<string> _endFailed = new(StringComparer.OrdinalIgnoreCase);

    private void AskEnd(string key)
    {
        _confirmEnd = key;
        _confirmTimer?.Stop();
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmTimer?.Stop();
            _confirmEnd = null;
            Evaluate();
        };
        _confirmTimer.Start();
        Evaluate();
    }

    private void End(string key)
    {
        _confirmTimer?.Stop();
        _confirmEnd = null;
        // Checked again at the press: a window may have come back (the player reopened it), and the game's names may
        // have arrived since the line was drawn.
        var r = _reading;
        if (_gameProcesses.Contains(key) || WindowsCore.Contains(key) || r?.Windowed?.Contains(key) == true)
        {
            Evaluate();
            return;
        }
        if (!PcResources.EndProcesses(key)) _endFailed.Add(key);
        // The next scan sees it gone and drops it from the list.
        Evaluate();
    }

    /// <summary>"Bỏ qua": the player keeps this app on purpose. Never named again, on this PC, until "Theo dõi lại".</summary>
    private void Ignore(TopProcess app)
    {
        var ignored = new Dictionary<string, string>(_ignored, StringComparer.OrdinalIgnoreCase) { [app.Key] = app.Name };
        _ignored = ignored;
        IgnoredAppsStore.Save(ignored);
        foreach (var named in _named) named.Remove(app.Key);
        Evaluate();
    }

    private void Unignore(string key)
    {
        var ignored = new Dictionary<string, string>(_ignored, StringComparer.OrdinalIgnoreCase);
        if (!ignored.Remove(key)) return;
        _ignored = ignored;
        IgnoredAppsStore.Save(ignored);
        Evaluate();
    }

    private static string Game(Reading r, double share) => r.Playing ? Loc.F("check.res.gameShare", Pct(share)) : "";
}

/// <summary>
/// The rules behind the "Tài nguyên máy" line, apart from the item so they can be checked without a UI
/// (pc-checklist-handoff.md, item 4 criteria). See <see cref="ResourceCheck"/> for why they look at the game's share.
/// </summary>
internal static class ResourceRules
{
    // ---- thresholds (pc-checklist-handoff.md "Item 4 criteria"); CPU, GPU and disk on a 10 s average.

    /// <summary>The game takes at least this much of the PC: "heavy here", so a little background already costs FPS.</summary>
    internal const double HeavyGameCpu = 25, HeavyGameGpu = 50;

    /// <summary>Background allowed beside a heavy / a light game.</summary>
    internal const double BackgroundWarnHeavy = 10, BackgroundWarnLight = 25;

    /// <summary>The machine is full: any background at all beside it is critical.</summary>
    internal const double CpuFull = 90, GpuFull = 95;

    /// <summary>No game: this much load before the game even starts.</summary>
    internal const double IdleWarn = 50, IdleCritical = 85, IdleGpuWarn = 40, IdleGpuCritical = 80;

    internal const double DiskWarnMs = 20, DiskCriticalMs = 50;

    /// <summary>Free RAM while playing / before playing (a heavy game still has to load into it).</summary>
    internal const double RamWarnPlaying = 2048, RamCriticalPlaying = 1024, RamWarnIdle = 4096, RamCriticalIdle = 2048;

    /// <summary>The game's own disk reads are loading, not a problem: background below this is ignored for disk.</summary>
    internal const double BackgroundDiskMb = 1;

    /// <summary>
    /// 10 s of the machine, split into the game's part, Windows' own part (dwm and co, never background) and the rest.
    /// Game* and Windows* are 0 and Playing is false when no game runs;
    /// Top* are the heaviest background processes for each resource, biggest first, Running every process name and
    /// Windowed those with a window on screen, when a per-process scan ran (empty / null otherwise).
    /// </summary>
    internal sealed record Reading(
        bool Playing,
        double Cpu, double GameCpu, double WindowsCpu,
        double? Gpu, double GameGpu, double WindowsGpu,
        double DiskMs, double BackgroundDiskMb,
        double RamFreeMb,
        IReadOnlyList<TopProcess> TopCpu, IReadOnlyList<TopProcess> TopGpu,
        IReadOnlyList<TopProcess> TopDisk, IReadOnlyList<TopProcess> TopRam,
        IReadOnlySet<string>? Running = null, IReadOnlySet<string>? Windowed = null)
    {
        public double BackgroundCpu => Math.Max(0, Cpu - GameCpu - WindowsCpu);
        public double? BackgroundGpu => Gpu is { } g ? Math.Max(0, g - GameGpu - WindowsGpu) : null;
    }

    // ------------------------------------------------------------ judging

    /// <summary>
    /// One resource's verdict and, on a problem, the line saying why. Background apps using at least NameAtLeast are
    /// listed under the line, shown with Format; null when there is nobody to name (the game itself).
    /// </summary>
    internal sealed record Verdict(CheckSeverity Severity, string? Line, bool GameItself = false,
        double? NameAtLeast = null, Func<double, string>? Format = null);

    private static readonly Verdict Fine = new(CheckSeverity.Good, null);

    internal static Verdict JudgeCpu(Reading r)
    {
        if (!r.Playing)
        {
            var sev = Grade(r.Cpu, IdleWarn, IdleCritical);
            return sev == CheckSeverity.Good ? Fine
                : new Verdict(sev, Loc.F("check.res.cpuBusy", Pct(r.Cpu)), NameAtLeast: 5, Format: Pct);
        }

        var background = r.BackgroundCpu;
        var allowed = r.GameCpu >= HeavyGameCpu ? BackgroundWarnHeavy : BackgroundWarnLight;
        if (r.Cpu >= CpuFull && background >= BackgroundWarnHeavy)
            return new Verdict(CheckSeverity.Critical, Loc.F("check.res.cpuBackground", Pct(background)), NameAtLeast: 3, Format: Pct);
        if (background >= allowed)
            return new Verdict(CheckSeverity.Warning, Loc.F("check.res.cpuBackground", Pct(background)), NameAtLeast: 3, Format: Pct);
        if (r.Cpu >= CpuFull)
            return new Verdict(CheckSeverity.Warning, Loc.F("check.res.cpuGame", Pct(r.Cpu)), GameItself: true);
        return Fine;
    }

    internal static Verdict JudgeGpu(Reading r)
    {
        if (r.Gpu is not { } gpu) return Fine;
        if (!r.Playing)
        {
            var sev = Grade(gpu, IdleGpuWarn, IdleGpuCritical);
            return sev == CheckSeverity.Good ? Fine
                : new Verdict(sev, Loc.F("check.res.gpuBusy", Pct(gpu)), NameAtLeast: 5, Format: Pct);
        }

        var background = r.BackgroundGpu ?? 0;
        var allowed = r.GameGpu >= HeavyGameGpu ? BackgroundWarnHeavy : BackgroundWarnLight;
        if (gpu >= GpuFull && background >= BackgroundWarnHeavy)
            return new Verdict(CheckSeverity.Critical, Loc.F("check.res.gpuBackground", Pct(background)), NameAtLeast: 3, Format: Pct);
        if (background >= allowed)
            return new Verdict(CheckSeverity.Warning, Loc.F("check.res.gpuBackground", Pct(background)), NameAtLeast: 3, Format: Pct);
        return Fine; // a game using the whole GPU is what a GPU is for
    }

    internal static Verdict JudgeDisk(Reading r)
    {
        var sev = Grade(r.DiskMs, DiskWarnMs, DiskCriticalMs);
        if (sev == CheckSeverity.Good) return Fine;
        // While playing, a slow disk with nothing else on it is the game loading its own map: nothing to close.
        if (r.Playing && r.BackgroundDiskMb < BackgroundDiskMb) return Fine;
        return new Verdict(sev, Loc.F("check.res.diskSlow", Ms(r.DiskMs)), NameAtLeast: 1, Format: Rate);
    }

    internal static Verdict JudgeRam(Reading r)
    {
        var sev = r.Playing
            ? r.RamFreeMb < RamCriticalPlaying ? CheckSeverity.Critical : r.RamFreeMb < RamWarnPlaying ? CheckSeverity.Warning : CheckSeverity.Good
            : r.RamFreeMb < RamCriticalIdle ? CheckSeverity.Critical : r.RamFreeMb < RamWarnIdle ? CheckSeverity.Warning : CheckSeverity.Good;
        return sev == CheckSeverity.Good ? Fine
            : new Verdict(sev, Loc.F("check.res.ramLow", Size(r.RamFreeMb)), NameAtLeast: 200, Format: Size);
    }

    private static CheckSeverity Grade(double value, double warn, double critical) =>
        value >= critical ? CheckSeverity.Critical : value >= warn ? CheckSeverity.Warning : CheckSeverity.Good;

    internal static string Pct(double value) => value.ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string Ms(double ms) => ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " ms";

    internal static string Size(double mb) => mb >= 1024
        ? (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : mb.ToString("0", CultureInfo.InvariantCulture) + " MB";

    internal static string Rate(double mbPerSecond) => mbPerSecond.ToString("0", CultureInfo.InvariantCulture) + " MB/s";
}
