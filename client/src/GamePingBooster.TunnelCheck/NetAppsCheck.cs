using System.Diagnostics;
using GamePingBooster.Service.Network;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The PC tune-up panel's "apps using the network" count, against a real download (Administrator: switching TCP
/// counters on needs it).
///
///     dotnet run --project client/src/GamePingBooster.TunnelCheck -- netapps
///
/// Starts curl on a large download and checks the count names curl at the rate the adapters received (never above it, at most 20 % below).
/// </summary>
internal static partial class Program
{
    private static int NetAppsMain(string[] args)
    {
        var failures = 0;
        foreach (var family in new[] { "-4" })
        {
            using var curl = Process.Start(new ProcessStartInfo("curl.exe",
                $"{family} -s -o NUL https://proof.ovh.net/files/1Gb.dat")
            { UseShellExecute = false, CreateNoWindow = true })!;
            Thread.Sleep(2000);
            var all = GamePingBooster.Service.Native.TcpTraffic.Established();
            var mine = all.Where(c => c.Pid == (uint)curl.Id).ToList();
            Console.WriteLine($"{family}: {all.Count} established, {all.Count(c => c.V6)} v6, curl {curl.Id} has {mine.Count}, exited={curl.HasExited}");
            using var apps = new NetworkApps(() => [], () => false, () => null, Console.WriteLine);
            // The adapter curl's connection leaves by - not every adapter: Hyper-V switches count the same bytes again.
            var local = mine.FirstOrDefault()?.Local;
            var line = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(local)));
            Console.WriteLine($"   line adapter: {line?.Name ?? "?"}");
            long NicIn() => line?.GetIPStatistics().BytesReceived ?? 0;
            var nic0 = NicIn();
            var sw = Stopwatch.StartNew();
            var (list, error) = apps.CountAsync(CancellationToken.None).GetAwaiter().GetResult();
            var nicMbps = (NicIn() - nic0) / 1048576.0 / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"   the line adapter received {nicMbps:0.00} MB/s over the same {sw.Elapsed.TotalSeconds:0.0} s");
            Console.WriteLine($"{family}: counted in {sw.ElapsedMilliseconds} ms, error={error ?? "-"}");
            foreach (var a in list ?? []) Console.WriteLine($"   {a.Key,-24} {a.Name,-32} down {a.DownMbps,7:0.00}  up {a.UpMbps,6:0.00} MB/s");
            var hit = list?.FirstOrDefault(a => a.Key.Equals("curl", StringComparison.OrdinalIgnoreCase));
            // Never more than the line got; at most a little less (the rest of the PC's traffic is on the line too).
            var ok = hit is { } h && h.DownMbps > 0.1 && h.DownMbps <= nicMbps * 1.05 && h.DownMbps >= nicMbps - Math.Max(0.15, 0.2 * nicMbps);
            if (!curl.HasExited) curl.Kill();
            Console.WriteLine(ok ? $"   PASS curl named at {hit!.DownMbps:0.0} MB/s" : "   FAIL curl not named");
            if (!ok) failures++;
        }
        return failures;
    }
}

/// <summary>
/// Pausing Windows Update and every way back (Administrator):
///
///     dotnet run --project client/src/GamePingBooster.TunnelCheck -- pauseupdates
///
/// Changes the real wuauserv and DoSvc for a few seconds and checks each is put back exactly as it was: when the game
/// closes, and after a run that died without undoing it.
/// </summary>
internal static partial class Program
{
    private static readonly string[] UpdateServiceNames = ["wuauserv", "DoSvc"];

    private static string ServiceStates() => string.Join(", ", UpdateServiceNames.Select(n =>
    {
        using var sc = new System.ServiceProcess.ServiceController(n);
        return $"{n} {sc.StartType}/{sc.Status}";
    }));

    private static int PauseUpdatesMain(string[] args)
    {
        var failures = 0;
        void Expect(string what, bool ok)
        {
            Console.WriteLine($"   {(ok ? "PASS" : "FAIL")} {what}");
            if (!ok) failures++;
        }

        var before = ServiceStates();
        Console.WriteLine($"before:        {before}");

        // 1. Paused while a game runs, back by itself when it closes.
        var game = true;
        using (var apps = new NetworkApps(() => [], () => game, () => null, Console.WriteLine))
        {
            Expect("refused when no game runs", new NetworkApps(() => [], () => false, () => null, _ => { }).PauseUpdates() is not null);
            var error = apps.PauseUpdates();
            Console.WriteLine($"paused:        {ServiceStates()}");
            Expect("paused", error is null && apps.UpdatesPaused && ServiceStates().Contains("wuauserv Disabled/Stopped"));
            // DoSvc cannot be disabled: started again by hand (Windows then puts wuauserv back too), the watch must hold both.
            using (var doSvc = new System.ServiceProcess.ServiceController("DoSvc")) doSvc.Start();
            Thread.Sleep(12000);
            Console.WriteLine($"DoSvc started: {ServiceStates()}");
            Expect("both held stopped", ServiceStates() == "wuauserv Disabled/Stopped, DoSvc Automatic/Stopped");
            game = false;
            Thread.Sleep(7000);
            Console.WriteLine($"game closed:   {ServiceStates()}");
            Expect("resumed when the game closed", !apps.UpdatesPaused);
        }
        Thread.Sleep(2000);
        Expect("start types back as before", StartTypes(ServiceStates()) == StartTypes(before));

        // 2. A run that dies paused: the next start puts the start types back.
        game = true;
        var dead = new NetworkApps(() => [], () => game, () => null, Console.WriteLine);
        dead.PauseUpdates();
        Console.WriteLine($"paused, died:  {ServiceStates()}");
        NetworkApps.RestoreLeftovers(Console.WriteLine);
        Console.WriteLine($"next start:    {ServiceStates()}");
        Expect("start types back after a crash", StartTypes(ServiceStates()) == StartTypes(before));
        return failures;
    }

    private static string StartTypes(string states) =>
        string.Join(",", states.Split(", ").Select(s => s.Split('/')[0]));
}
