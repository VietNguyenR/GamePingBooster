using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using GamePingBooster.EtwWatch;
using GamePingBooster.Service.Discovery;

// gpb-etwwatch: which servers a game talks to, read from ETW instead of a packet capture.
//
// The prototype of the service's GameDestinationRecorder, and still the way to check it: the ETW
// code is the service's own, compiled in. It answers two questions by running next to
// `./gpb capture` during the same match:
//   1. Does ETW find the same servers and datacentre probes that capture finds?
//   2. What does it cost? Every UDP packet on the machine becomes an event, the game's and
//      everybody else's, so the status line reports events per second, events lost, this
//      tool's CPU and the time spent per event.
//
// Nothing here touches the game: the process list is read the way Task Manager reads it, and the
// events come from the TCP/IP stack. It works the same with the booster connected or not - the
// game's socket addresses its packets to the game server either way; the tunnel's own packets
// belong to gpb-service, not to the game.
//
// Findings go to their own file (--report), never into observed.txt or the landmark list. That
// only changes once this has been shown to agree with capture.

var culture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

Options options;
try
{
    options = Options.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.Error.WriteLine("ETW network events need Administrator rights. Run this from an Administrator terminal.");
    return 2;
}

var ownPid = (uint)Environment.ProcessId;
var table = new FlowTable(ownPid);
var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    // Handled rather than killed: stopping mid-match should still produce the report, as it does
    // for capture.
    e.Cancel = true;
    stop.Set();
};

KernelNetworkTrace trace;
try
{
    trace = KernelNetworkTrace.Start("GamePingBooster-EtwWatch", table, Log);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// A session outlives its process. Closing the console window skips `finally`, not this.
AppDomain.CurrentDomain.ProcessExit += (_, _) => trace.Dispose();

try
{
    Say($"ETW session {trace.SessionName} started" +
        (trace.EventIdFilterApplied ? " (UDP event ids only)." : " WITHOUT the event-id filter - TCP events are delivered and dropped."));

    DecodingRules rules;
    try
    {
        rules = SelfTest.Run(table, Log);
    }
    catch (Exception ex) when (ex is InvalidOperationException or SocketException)
    {
        Console.Error.WriteLine("Self-test failed: " + ex.Message);
        return 1;
    }
    table.Rules = rules;
    Say("Self-test passed - " + rules.Evidence);
    foreach (var layout in trace.DescribeLayouts()) Detail("layout " + layout);

    var known = KnownAddresses.Load(options);
    foreach (var note in known.Notes) Detail(note);

    Say($"Waiting for {string.Join(" / ", options.Processes.Select(p => p + ".exe"))} - Ctrl+C to stop.");

    Session? session = null;
    var status = new Meter(trace, table);
    var nextStatus = Stopwatch.GetTimestamp() + Ticks(options.StatusInterval);

    while (!stop.Wait(TimeSpan.FromSeconds(1)))
    {
        var pids = FindGame(options.Processes);

        if (session is null && pids.Count > 0)
        {
            session = new Session(pids, new Meter(trace, table));
            table.SetGamePids(pids);
            table.RemoveAllExcept(pids);
            Say($"{options.GameName} started (pid {string.Join(", ", pids)}) - recording.");
            status = new Meter(trace, table);
            nextStatus = Stopwatch.GetTimestamp() + Ticks(options.StatusInterval);
        }
        else if (session is not null && pids.Count > 0)
        {
            if (pids.Except(session.Pids).Any())
            {
                session.Pids.UnionWith(pids);
                table.SetGamePids(session.Pids);
            }
        }

        if (Stopwatch.GetTimestamp() >= nextStatus)
        {
            PrintStatus(status, session);
            status = new Meter(trace, table);
            nextStatus = Stopwatch.GetTimestamp() + Ticks(options.StatusInterval);
        }

        if (session is not null && pids.Count == 0)
        {
            Say($"{options.GameName} exited.");
            Report(session, rules, known);
            session = null;
            table.SetGamePids([]);
            table.Clear();
            Say($"Waiting for {string.Join(" / ", options.Processes.Select(p => p + ".exe"))} - Ctrl+C to stop.");
        }
    }

    if (session is not null)
    {
        Say("Stopped mid-session - reporting what was recorded.");
        Report(session, rules, known);
    }
    return 0;
}
finally
{
    trace.Dispose();
}

// ---------------------------------------------------------------------------------------------

static HashSet<uint> FindGame(List<string> processes)
{
    var pids = new HashSet<uint>();
    foreach (var name in processes)
    {
        foreach (var process in Process.GetProcessesByName(name))
        {
            pids.Add((uint)process.Id);
            process.Dispose();
        }
    }
    return pids;
}

void PrintStatus(Meter since, Session? session)
{
    var now = new Meter(trace, table);
    var d = now.Minus(since);
    var line = new StringBuilder();
    line.Append(culture, $"[{DateTime.Now:HH:mm:ss}] {(session is null ? "idle" : "game")}: ");
    line.Append(culture, $"UDP {d.UdpPerSecond:N0}/s (game {d.GamePerSecond:N0}/s)");
    if (d.OtherEvents > 0) line.Append(culture, $", other ids {d.OtherEvents:N0}");
    if (d.Undecoded > 0) line.Append(culture, $", undecoded {d.Undecoded:N0}");
    line.Append(culture, $", lost {d.EventsLost:N0} events/{d.BuffersLost:N0} buffers");
    line.Append(culture, $" | tool CPU {d.CpuPercentOfMachine:F2}% of machine, {d.CpuPercentOfCore:F1}% of a core");
    if (d.UdpEvents > 0) line.Append(culture, $", {d.NanosecondsPerEvent:N0} ns/event in callback");
    if (session is not null)
    {
        var rows = table.Snapshot(session.Pids);
        var servers = rows.Count(r => Verdict(r) == "server");
        line.Append(culture, $" | {rows.Count} destination(s), {servers} server(s)");
    }
    Console.WriteLine(line.ToString());
}

void Report(Session session, DecodingRules rules, KnownAddresses known)
{
    var end = new Meter(trace, table);
    var d = end.Minus(session.Start);
    // Loopback and LAN destinations are the game talking to its own launcher, anti-cheat or router.
    // None of them can be a game server, and the relay refuses private ranges anyway, so they are
    // counted and left out rather than listed as NEW.
    var all = table.Snapshot(session.Pids);
    var local = all.Count(r => Destinations.IsLocal(r.Address));
    var rows = all.Where(r => !Destinations.IsLocal(r.Address))
        .OrderByDescending(r => Verdict(r) switch { "server" => 2, "probe" => 1, _ => 0 })
        .ThenByDescending(r => r.Sent)
        .ToList();

    var started = session.StartedAt;
    var header =
        $"session {started:yyyy-MM-dd HH:mm:ss} -> {DateTime.Now:HH:mm:ss} ({d.Seconds / 60:F0} min), " +
        $"{options.GameName} pid {string.Join(",", session.Pids)}";
    var cost =
        $"{d.UdpEvents:N0} UDP events ({d.UdpPerSecond:N0}/s, game {d.GamePerSecond:N0}/s), " +
        $"lost {d.EventsLost:N0} events/{d.BuffersLost:N0} buffers, other ids {d.OtherEvents:N0}, undecoded {d.Undecoded:N0}, " +
        $"tool CPU {d.CpuPercentOfMachine:F2}% of machine ({d.CpuPercentOfCore:F1}% of a core), " +
        $"{d.NanosecondsPerEvent:N0} ns/event";
    if (table.Untracked > 0) cost += $", {table.Untracked:N0} events past the flow limit";
    if (rules.ReceiveRemoteIsDestination is null) cost += ", receives NOT counted (unverified)";
    if (local > 0) cost += $", {local} loopback/LAN destination(s) left out";

    Console.WriteLine();
    Say(header);
    Detail(cost);
    Console.WriteLine();

    var lines = Destinations.FormatTable(rows, Verdict, known.Describe, rules.Ipv6Verified);

    const int shown = 40;
    foreach (var line in lines.Take(shown + 1)) Console.WriteLine("    " + line);
    if (lines.Count > shown + 1) Detail($"... {lines.Count - shown - 1} more, all in the report file");

    var servers = rows.Where(r => Verdict(r) == "server").ToList();
    var probes = rows.Where(r => Verdict(r) == "probe").ToList();
    Console.WriteLine();
    Summarise("servers not in the observed file", servers.Where(r => !known.InObserved(r.Address)));
    Summarise("servers not covered by the profile's ranges", servers.Where(r => !known.InProfileRanges(r.Address)));
    if (options.ProbePort > 0)
    {
        Summarise($"probe endpoints (UDP {options.ProbePort}) not yet known as landmarks", probes.Where(r => !known.IsKnownLandmark(r.Address)));
    }

    if (options.ReportPath is { } path)
    {
        var fresh = !File.Exists(path);
        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
        if (fresh)
        {
            writer.WriteLine("# Game destinations recorded from ETW by gpb-etwwatch (./gpb etw).");
            writer.WriteLine("# A prototype running next to ./gpb capture: nothing reads this file automatically, and");
            writer.WriteLine("# nothing here has been routed or added to a profile. Compare it with capture's output.");
            writer.WriteLine("# sent = outbound packets (capture's count), verdict: server >= min packets, probe = only");
            writer.WriteLine("# the probe port, known = where the project already has the address.");
        }
        writer.WriteLine("#");
        writer.WriteLine("# " + header);
        writer.WriteLine("# " + cost);
        writer.WriteLine("# decoding: " + rules.Evidence);
        foreach (var line in lines) writer.WriteLine(line == lines[0] ? "# " + line : line);
        Detail($"written to {path}");
    }
}

void Summarise(string what, IEnumerable<FlowRow> rows)
{
    var list = rows.ToList();
    if (list.Count == 0)
    {
        Detail($"No {what}.");
        return;
    }
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"    {list.Count} {what}: {string.Join(", ", list.Select(r => r.Address))}");
    Console.ResetColor();
}

string Verdict(FlowRow row)
{
    if (options.ProbePort > 0 && row.Ports.Count > 0 && row.Ports.All(p => p == options.ProbePort)) return "probe";
    return row.Sent >= options.MinPackets ? "server" : "minor";
}

static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

static void Say(string message)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("==> " + message);
    Console.ResetColor();
}

static void Detail(string message)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("    " + message);
    Console.ResetColor();
}

static void Log(string message)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("    " + message);
    Console.ResetColor();
}

internal sealed class Session(HashSet<uint> pids, Meter start)
{
    public HashSet<uint> Pids { get; } = pids;
    public Meter Start { get; } = start;
    public DateTime StartedAt { get; } = DateTime.Now;
}

/// <summary>Every counter the cost report needs, read at one instant.</summary>
internal sealed class Meter
{
    private readonly long _timestamp = Stopwatch.GetTimestamp();
    private readonly long _udp, _game, _other, _undecoded, _callbackTicks;
    private readonly uint _eventsLost, _buffersLost;
    private readonly TimeSpan _cpu = Environment.CpuUsage.TotalTime;

    public Meter(KernelNetworkTrace trace, FlowTable table)
    {
        _udp = trace.UdpEvents;
        _game = table.GameEvents;
        _other = trace.OtherEvents;
        _undecoded = trace.UndecodedEvents;
        _callbackTicks = trace.CallbackTicks;
        (_eventsLost, _buffersLost, _) = trace.QueryLosses();
    }

    public Delta Minus(Meter earlier) => new(
        Seconds: Math.Max((_timestamp - earlier._timestamp) / (double)Stopwatch.Frequency, 0.001),
        UdpEvents: _udp - earlier._udp,
        GameEvents: _game - earlier._game,
        OtherEvents: _other - earlier._other,
        Undecoded: _undecoded - earlier._undecoded,
        CallbackTicks: _callbackTicks - earlier._callbackTicks,
        EventsLost: _eventsLost - earlier._eventsLost,
        BuffersLost: _buffersLost - earlier._buffersLost,
        Cpu: _cpu - earlier._cpu);
}

internal readonly record struct Delta(
    double Seconds, long UdpEvents, long GameEvents, long OtherEvents, long Undecoded,
    long CallbackTicks, long EventsLost, long BuffersLost, TimeSpan Cpu)
{
    public double UdpPerSecond => UdpEvents / Seconds;
    public double GamePerSecond => GameEvents / Seconds;
    public double CpuPercentOfCore => Cpu.TotalSeconds / Seconds * 100;
    public double CpuPercentOfMachine => CpuPercentOfCore / Environment.ProcessorCount;
    public double NanosecondsPerEvent => UdpEvents == 0 ? 0 : CallbackTicks * 1e9 / Stopwatch.Frequency / UdpEvents;
}
