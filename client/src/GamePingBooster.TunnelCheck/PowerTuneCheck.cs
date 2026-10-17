using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Native;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Network;
using Microsoft.Win32;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Item 5 of the PC tune-up panel, against the real Windows (run as SYSTEM, like the service:
/// psexec -s ... TunnelCheck powertune [nic-guid]).
///
///     powerread              what the app would read: plan, overlay, battery, the line card and every card's settings
///     powertune [nic-guid]   boosts and puts back the real power plan in every way it can end, then (with a card GUID,
///                            best a card that is not the line) turns its power saving off and back on through the
///                            real driver key and a real card restart
///
/// Every change is put back at the end, also when a check fails: the plan, the overlay and the card's value.
/// </summary>
internal static partial class Program
{
    private static int PowerReadMain(string[] args)
    {
        var s = PowerPlan.Read();
        Console.WriteLine($"plan {s.Scheme} type {s.Type} overlay {(s.OverlaySupported ? s.Overlay : "unsupported")} " +
                          $"battery {(s.OnBattery ? "on battery" : "mains/none")} saver {s.BatterySaver} fast {s.IsFast}");
        Console.WriteLine($"listed: {string.Join(", ", PowerPlan.ListedSchemes().Select(g => $"{g} ({PowerPlan.TypeOf(g)})"))}");
        Console.WriteLine($"boost target: {PowerPlan.BoostTarget(s)?.ToString() ?? "none (already fast or nothing faster)"}");
        var line = NicPowerSaving.LineCard();
        Console.WriteLine($"line card: {(line is null ? "none" : $"{line.DriverKey} {line.Description} on: {string.Join(", ", line.On.Select(o => $"{o.Name}={o.Current ?? "default"}"))}")}");
        using var cls = Registry.LocalMachine.OpenSubKey(NicPowerSaving.ClassKey)!;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4))
        {
            try
            {
                using var driver = cls.OpenSubKey(sub);
                if (driver?.GetValue("NetCfgInstanceId") is not string id) continue;
                var on = NicPowerSaving.SettingsOn(driver, sub);
                Console.WriteLine($"  {sub} {driver.GetValue("DriverDesc")} {id}: {(on.Count == 0 ? "-" : string.Join(", ", on.Select(o => $"{o.Keyword}={o.Current ?? "default"}")))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {sub}: unreadable ({ex.GetType().Name})");
            }
        }
        return 0;
    }

    private static int PowerTuneMain(string[] args)
    {
        var failures = 0;
        void Expect(string what, bool ok)
        {
            Console.WriteLine($"   {(ok ? "PASS" : "FAIL")} {what}");
            if (!ok) failures++;
        }
        string Plan()
        {
            var s = PowerPlan.Read();
            return $"{Name(s.Scheme)} overlay={OverlayName(PowerPlan.ActualOverlay())}/eff={OverlayName(PowerPlan.EffectiveOverlay())} fast={s.IsFast}";
        }
        var marker = Path.Combine(GamePingBooster.Service.ServiceConfig.DefaultDirectory, "power-boost");

        var originalScheme = PowerPlan.ActiveScheme()!.Value;
        var originalOverlay = PowerPlan.ActualOverlay();
        Console.WriteLine($"before: {Plan()}");
        try
        {
            var game = false;
            var state = TunnelState.Disconnected;
            void Start(Guid scheme)
            {
                PowerPlan.SetActive(PowerPlan.Balanced);
                PowerPlan.SetOverlay(PowerPlan.OverlayDefault);
                PowerPlan.SetActive(scheme);
            }

            Console.WriteLine("1. High performance while a game runs, back when it closes");
            Start(PowerPlan.Balanced);
            using (var tune = new PowerTune(() => game, () => state, Console.WriteLine))
            {
                Expect("refused outside a game session", tune.Boost() is not null && PowerPlan.ActiveScheme() == PowerPlan.Balanced);
                game = true;
                var error = tune.Boost();
                Console.WriteLine($"   boosted: {Plan()}");
                Expect("boosted to High performance", error is null && tune.Boosted && PowerPlan.ActiveScheme() == PowerPlan.HighPerformance);
                Expect("marker written", File.Exists(marker));
                game = false;
                Thread.Sleep(7000);
                Console.WriteLine($"   game closed: {Plan()}");
                Expect("back to Balanced when the game closed", !tune.Boosted && PowerPlan.ActiveScheme() == PowerPlan.Balanced);
                Expect("marker removed", !File.Exists(marker));
            }

            Console.WriteLine("2. Connected without a game, then boosting stops");
            Start(PowerPlan.Balanced);
            state = TunnelState.Connected;
            using (var tune = new PowerTune(() => game, () => state, Console.WriteLine))
            {
                Expect("boosted while connected", tune.Boost() is null && PowerPlan.ActiveScheme() == PowerPlan.HighPerformance);
                Thread.Sleep(6000);
                Expect("held while connected", tune.Boosted);
                state = TunnelState.Disconnected;
                Thread.Sleep(7000);
                Expect("back when boosting stopped", !tune.Boosted && PowerPlan.ActiveScheme() == PowerPlan.Balanced);
            }

            Console.WriteLine("3. The player picks a plan during the boost");
            Start(PowerPlan.Balanced);
            game = true;
            using (var tune = new PowerTune(() => game, () => state, Console.WriteLine))
            {
                tune.Boost();
                PowerPlan.SetActive(PowerPlan.PowerSaver);
                Thread.Sleep(7000);
                Expect("their plan left alone", !tune.Boosted && PowerPlan.ActiveScheme() == PowerPlan.PowerSaver && !File.Exists(marker));
                game = false;
            }

            Console.WriteLine("4. A run that dies boosted");
            Start(PowerPlan.Balanced);
            game = true;
            var dead = new PowerTune(() => game, () => state, Console.WriteLine);
            dead.Boost();
            Console.WriteLine($"   boosted, died: {Plan()}");
            PowerTune.RestoreLeftovers(Console.WriteLine);
            Console.WriteLine($"   next start: {Plan()}");
            Expect("back to Balanced after a crash", PowerPlan.ActiveScheme() == PowerPlan.Balanced && !File.Exists(marker));
            dead.Dispose(); // its watch: the plan is not its own any more, so it leaves it
            Expect("the dead run's watch left it alone", PowerPlan.ActiveScheme() == PowerPlan.Balanced);

            Console.WriteLine("5. No High performance listed: Balanced at Best performance");
            (Guid, Guid?)? OverlayPath(PowerPlan.State s) => s.IsFast ? null : (PowerPlan.Balanced, PowerPlan.OverlayBestPerformance);
            Start(PowerPlan.Balanced);
            using (var tune = new PowerTune(() => game, () => state, Console.WriteLine, OverlayPath))
            {
                var error = tune.Boost();
                Console.WriteLine($"   boosted: {Plan()}");
                Expect("Best performance overlay on", error is null && PowerPlan.EffectiveOverlay() == PowerPlan.OverlayBestPerformance && PowerPlan.Read().IsFast);
                game = false;
                Thread.Sleep(7000);
                Console.WriteLine($"   game closed: {Plan()}");
                Expect("overlay back to default", PowerPlan.ActualOverlay() == PowerPlan.OverlayDefault && PowerPlan.ActiveScheme() == PowerPlan.Balanced);
            }

            Console.WriteLine("6. From Power saver through the overlay path");
            Start(PowerPlan.PowerSaver);
            game = true;
            using (var tune = new PowerTune(() => game, () => state, Console.WriteLine, OverlayPath))
            {
                tune.Boost();
                Console.WriteLine($"   boosted: {Plan()}");
                Expect("Balanced at Best performance", PowerPlan.ActiveScheme() == PowerPlan.Balanced && PowerPlan.Read().IsFast);
                tune.Unboost("test: Restore pressed");
                Console.WriteLine($"   restored: {Plan()}");
                Expect("Power saver back, overlay default", PowerPlan.ActiveScheme() == PowerPlan.PowerSaver && PowerPlan.ActualOverlay() == PowerPlan.OverlayDefault);
            }

            Console.WriteLine("7. Already fast; the service stopping while boosted");
            Start(PowerPlan.HighPerformance);
            using (var tune = new PowerTune(() => game, () => state, Console.WriteLine))
                Expect("nothing to do on High performance", tune.Boost() is null && !tune.Boosted);
            Start(PowerPlan.Balanced);
            var stopping = new PowerTune(() => game, () => state, Console.WriteLine);
            stopping.Boost();
            stopping.Dispose();
            Expect("put back when the service stops", PowerPlan.ActiveScheme() == PowerPlan.Balanced && !File.Exists(marker));
        }
        finally
        {
            PowerPlan.SetActive(PowerPlan.Balanced);
            PowerPlan.SetOverlay(originalOverlay ?? PowerPlan.OverlayDefault);
            PowerPlan.SetActive(originalScheme);
            Console.WriteLine($"after:  {Plan()}");
        }
        Expect("plan exactly as before", PowerPlan.ActiveScheme() == originalScheme && PowerPlan.ActualOverlay() == originalOverlay);

        if (args.Length > 0 && Guid.TryParse(args[0], out var nic)) failures += NicCheck(nic);
        return failures;
    }

    private static int NicCheck(Guid nic)
    {
        var failures = 0;
        void Expect(string what, bool ok)
        {
            Console.WriteLine($"   {(ok ? "PASS" : "FAIL")} {what}");
            if (!ok) failures++;
        }
        var card = NicPowerSaving.CardFor(nic);
        if (card is null)
        {
            Console.WriteLine($"card {nic} not found");
            return 1;
        }
        const string Keyword = "DMACoalescing";
        var path = NicPowerSaving.ClassKey + @"\" + card.DriverKey;
        using var driver = Registry.LocalMachine.OpenSubKey(path, writable: true)!;
        var original = driver.GetValue(Keyword);
        var originalKind = original is null ? RegistryValueKind.Unknown : driver.GetValueKind(Keyword);
        Console.WriteLine($"8. Card {card.DriverKey} {card.Description}: {Keyword} was {original ?? "(none)"} {originalKind}");
        var marker = Path.Combine(GamePingBooster.Service.ServiceConfig.DefaultDirectory, "nic-power");
        try
        {
            driver.SetValue(Keyword, "250", RegistryValueKind.String); // "power saving on", as the test's starting point
            var seen = NicPowerSaving.CardFor(nic)!;
            Expect("power saving seen", seen.On.Any(s => s.Keyword == Keyword && s.Current == "250"));

            var game = true;
            var tunnel = TunnelState.Disconnected;
            using var tune = new PowerTune(() => game, () => tunnel, Console.WriteLine,
                lineCard: () => NicPowerSaving.CardFor(nic));
            Expect("refused while a game runs", tune.TurnOffNicPowerSavingAsync().GetAwaiter().GetResult() == "game-running"
                                                && (string?)driver.GetValue(Keyword) == "250");
            game = false;
            tunnel = TunnelState.Connected;
            Expect("refused while the tunnel is up", tune.TurnOffNicPowerSavingAsync().GetAwaiter().GetResult() == "tunnel-up"
                                                     && (string?)driver.GetValue(Keyword) == "250");
            tunnel = TunnelState.Disconnected;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var error = tune.TurnOffNicPowerSavingAsync().GetAwaiter().GetResult();
            Console.WriteLine($"   turned off in {watch.ElapsedMilliseconds} ms, result {error ?? "ok"}; value {driver.GetValue(Keyword)}");
            Expect("turned off and restarted", error is null && (string?)driver.GetValue(Keyword) == "0" && tune.NicChanged);
            Expect("nothing left on", NicPowerSaving.CardFor(nic)!.On.All(s => s.Keyword != Keyword));
            Expect("old value recorded", File.ReadAllText(marker).Contains($"{card.DriverKey}\t{Keyword}\t250"));
            Expect("a second press keeps the old record", tune.TurnOffNicPowerSavingAsync().GetAwaiter().GetResult() is null
                                                          && File.ReadAllText(marker).Contains($"{card.DriverKey}\t{Keyword}\t250"));

            // Planted lines must be ignored: a keyword not on the list, and a value the driver does not offer.
            File.AppendAllText(marker, $"\n{card.DriverKey}\tNetworkAddress\tAABBCC\n{card.DriverKey}\tULPMode\t77");
            var addressBefore = driver.GetValue("NetworkAddress");
            game = true;
            Expect("restore refused while a game runs", tune.RestoreNicAsync().GetAwaiter().GetResult() == "game-running" && tune.NicChanged);
            game = false;
            error = tune.RestoreNicAsync().GetAwaiter().GetResult();
            Console.WriteLine($"   restored, result {error ?? "ok"}; value {driver.GetValue(Keyword)}");
            Expect("old value back", error is null && (string?)driver.GetValue(Keyword) == "250" && !tune.NicChanged && !File.Exists(marker));
            Expect("planted lines ignored", Equals(driver.GetValue("NetworkAddress"), addressBefore) && driver.GetValue("ULPMode") is null);
        }
        finally
        {
            if (original is null) driver.DeleteValue(Keyword, false);
            else driver.SetValue(Keyword, original, originalKind);
            var (result, code) = DeviceRestart.Restart(card.DriverKey);
            Console.WriteLine($"after: {Keyword} = {driver.GetValue(Keyword) ?? "(none)"}; restart {result} {code}");
        }
        Expect("card value exactly as before", Equals(driver.GetValue(Keyword), original));
        return failures;
    }

    /// <summary>cardrestart NNNN: restarts one card the way a "Tắt tiết kiệm điện" press does, timing it.</summary>
    private static int CardRestartMain(string[] args)
    {
        if (args.Length == 0) return 1;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (result, code) = DeviceRestart.Restart(args[0]);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} card {args[0]}: {result} {code} in {watch.ElapsedMilliseconds} ms");
        return result == DeviceRestart.Result.Restarted ? 0 : 1;
    }

    private static string Name(Guid g) =>
        g == PowerPlan.Balanced ? "Balanced" : g == PowerPlan.HighPerformance ? "HighPerformance"
        : g == PowerPlan.PowerSaver ? "PowerSaver" : g.ToString();

    private static string OverlayName(Guid? g) =>
        g is null ? "unsupported" : g == PowerPlan.OverlayBestPerformance ? "Best" : g == Guid.Empty ? "default" : g.ToString()!;
}
