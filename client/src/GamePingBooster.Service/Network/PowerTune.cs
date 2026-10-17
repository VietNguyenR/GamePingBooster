using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Native;
using GamePingBooster.Service.Native;
using Microsoft.Win32;

namespace GamePingBooster.Service.Network;

/// <summary>
/// The service's half of item 5 of the PC tune-up panel, "Chế độ nguồn + card mạng" (pc-checklist-handoff.md). Two
/// changes, each written down before it is made so it can always be put back:
///
/// 1. The power plan boost - temporary. Switches to High performance (or Balanced at "Best performance" where Windows
///    lists no High performance; see <see cref="PowerPlan.BoostTarget"/>) and puts the player's plan back when the game
///    closes; when no game was seen, when boosting stops; on "Khôi phục"; when the service stops; and at the next start
///    after a crash (<see cref="RestoreLeftovers"/>). A plan the player changed themselves in the meantime is left
///    alone - only what this service set is ever put back.
///
/// 2. The network card's own power saving (NicPowerSaving) - lasting, as agreed with the owner: changing it restarts
///    the card, so it is refused while a game runs or the tunnel is up, and it stays changed until "Khôi phục". Measured
///    2026-10-17: restarting the line card under a connected tunnel left the uplink failing ("An invalid argument was
///    supplied", 610 packets) for 95 s, until the relay-silence check reconnected - so the card is only touched while
///    nothing rides on it. The record of the old
///    values (<see cref="NicMarkerPath"/>) is what "Khôi phục" reads; a value the driver had at its default is removed
///    again rather than written.
/// </summary>
internal sealed class PowerTune : IDisposable
{
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(5);

    private static string BoostMarkerPath => Path.Combine(ServiceConfig.DefaultDirectory, "power-boost");
    private static string NicMarkerPath => Path.Combine(ServiceConfig.DefaultDirectory, "nic-power");

    /// <summary>Written in a marker for "the driver had no value of its own".</summary>
    private const string Absent = "-";

    private readonly Func<bool> _gameRunning;
    private readonly Func<TunnelState> _tunnelState;
    private readonly Action<string> _log;
    private readonly Func<PowerPlan.State, (Guid Scheme, Guid? Overlay)?> _boostTarget;
    private readonly Func<NicPowerSaving.Card?> _lineCard;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _nic = new(1, 1);
    private Timer? _watch;
    private BoostRecord? _boost;
    private bool _sawGame;
    private volatile bool _nicChanged;
    private bool _disposed;

    /// <param name="boostTarget">Tests only: what a boost switches to (default <see cref="PowerPlan.BoostTarget"/>).</param>
    /// <param name="lineCard">Tests only: the card to change (default the internet line's).</param>
    public PowerTune(Func<bool> gameRunning, Func<TunnelState> tunnelState, Action<string> log,
        Func<PowerPlan.State, (Guid Scheme, Guid? Overlay)?>? boostTarget = null, Func<NicPowerSaving.Card?>? lineCard = null)
    {
        _gameRunning = gameRunning;
        _tunnelState = tunnelState;
        _log = log;
        _boostTarget = boostTarget ?? PowerPlan.BoostTarget;
        _lineCard = lineCard ?? NicPowerSaving.LineCard;
        _nicChanged = ReadNicMarker().Count > 0;
    }

    /// <summary>The power plan is boosted by this service right now.</summary>
    public bool Boosted => Volatile.Read(ref _boost) is not null;

    /// <summary>The network card's power saving was turned off by this app and can be put back.</summary>
    public bool NicChanged => _nicChanged;

    /// <summary>Raised when the boost starts or ends (also by itself, when the game closes) or the card is changed.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------ power plan

    /// <summary>What the boost changed: the plan before and the overlay before (null = not touched), and what it set.</summary>
    private sealed record BoostRecord(Guid FromScheme, Guid? FromOverlay, Guid SetScheme, Guid? SetOverlay);

    /// <summary>Boosts the plan until the game closes. Null when boosted (or already fast), else why not.</summary>
    public string? Boost()
    {
        lock (_gate)
        {
            if (_disposed) return "The service is stopping.";
            if (_boost is not null) return null;
            var game = _gameRunning();
            if (!game && _tunnelState() is not (TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting))
                return "Not boosting - the plan is switched only for a game session.";

            var now = PowerPlan.Read();
            if (!now.Readable) return "Windows would not say which power plan is active.";
            if (now.IsFast) return null;
            if (_boostTarget(now) is not { } target) return "This PC lists no faster power plan.";

            var fromOverlay = target.Overlay is null ? (Guid?)null : PowerPlan.ActualOverlay() ?? Guid.Empty;
            var boost = new BoostRecord(now.Scheme, fromOverlay, target.Scheme, target.Overlay);

            // Written first: whatever happens next, the next start knows what to put back.
            try
            {
                AtomicFile.WriteAllText(BoostMarkerPath, Serialize(boost));
            }
            catch (Exception ex)
            {
                return $"Could not write {BoostMarkerPath} ({ex.Message}) - nothing was changed.";
            }

            if (target.Scheme != now.Scheme && PowerPlan.SetActive(target.Scheme) is var e1 and not 0)
            {
                DeleteBoostMarker();
                return $"Windows would not switch the power plan (error {e1}).";
            }
            if (target.Overlay is { } overlay && PowerPlan.SetOverlay(overlay) is var e2 and not 0)
            {
                PutBackLocked(boost);
                return $"Windows would not set the power mode (error {e2}).";
            }

            // A policy (a company PC) can put the plan straight back; then there is nothing to hold.
            if (!PowerPlan.Read().IsFast)
            {
                PutBackLocked(boost);
                return "Windows kept the old power plan (set by a policy?).";
            }

            _boost = boost;
            _sawGame = game;
            _watch = new Timer(_ => Watch(), null, WatchEvery, WatchEvery);
            _log($"Power: plan boosted for the game ({now.Scheme} {now.Type} -> {target.Scheme}" +
                 (target.Overlay is null ? "" : $" + overlay {target.Overlay}") + ").");
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>Puts the plan back as it was. Safe when nothing is boosted.</summary>
    public void Unboost(string why)
    {
        lock (_gate)
        {
            if (_boost is not { } boost) return;
            _watch?.Dispose();
            _watch = null;
            _boost = null;
            PutBackLocked(boost);
            _log($"Power: plan put back ({why}).");
        }
        Changed?.Invoke();
    }

    private void Watch()
    {
        try
        {
            BoostRecord? boost;
            lock (_gate) boost = _boost;
            if (boost is null) return;

            // The player picked a plan themselves: theirs now, nothing to put back.
            if (PowerPlan.ActiveScheme() is { } active && active != boost.SetScheme)
            {
                lock (_gate)
                {
                    if (_boost != boost) return;
                    _watch?.Dispose();
                    _watch = null;
                    _boost = null;
                    DeleteBoostMarker();
                }
                _log($"Power: the plan was changed to {active} during the boost - left as it is.");
                Changed?.Invoke();
                return;
            }

            if (_gameRunning())
            {
                _sawGame = true;
                return;
            }
            if (_sawGame) Unboost("the game closed");
            else if (_tunnelState() is TunnelState.Disconnected or TunnelState.Faulted) Unboost("boosting stopped before a game ran");
        }
        catch (Exception ex)
        {
            _log($"Power: could not check the game ({ex.Message}).");
        }
    }

    /// <summary>
    /// Puts back what <paramref name="boost"/> set, as far as it is still what this service set: the overlay first
    /// (it belongs to the Balanced plan that is active now), then the plan. Removes the marker.
    /// </summary>
    private void PutBackLocked(BoostRecord boost)
    {
        var active = PowerPlan.ActiveScheme();
        if (active == boost.SetScheme)
        {
            if (boost.SetOverlay is not null && boost.FromOverlay is { } fromOverlay
                && PowerPlan.ActualOverlay() == boost.SetOverlay && PowerPlan.SetOverlay(fromOverlay) is var e1 and not 0)
                _log($"Power: could not put the power mode back to {fromOverlay} (error {e1}).");

            if (boost.FromScheme != boost.SetScheme && PowerPlan.SetActive(boost.FromScheme) is var e2 and not 0)
            {
                // The old plan is gone (deleted meanwhile): Balanced is on every Windows and is what Windows picks itself.
                _log($"Power: could not switch back to {boost.FromScheme} (error {e2}) - switching to Balanced.");
                PowerPlan.SetActive(PowerPlan.Balanced);
            }
        }
        else
        {
            _log($"Power: the active plan is {active?.ToString() ?? "unknown"}, not the one set - left as it is.");
        }
        DeleteBoostMarker();
    }

    private static string Serialize(BoostRecord b) =>
        $"from\t{b.FromScheme}\nfromOverlay\t{b.FromOverlay?.ToString() ?? Absent}\nset\t{b.SetScheme}\nsetOverlay\t{b.SetOverlay?.ToString() ?? Absent}";

    private static BoostRecord? ReadBoostMarker()
    {
        try
        {
            if (!File.Exists(BoostMarkerPath)) return null;
            var fields = File.ReadAllLines(BoostMarkerPath)
                .Select(l => l.Split('\t'))
                .Where(f => f.Length == 2)
                .ToDictionary(f => f[0], f => f[1]);
            Guid? Optional(string key) => fields.GetValueOrDefault(key) is { } v && Guid.TryParse(v, out var g) ? g : null;
            if (Optional("from") is not { } from || Optional("set") is not { } set) return null;
            return new BoostRecord(from, Optional("fromOverlay"), set, Optional("setOverlay"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void DeleteBoostMarker()
    {
        try
        {
            File.Delete(BoostMarkerPath);
        }
        catch (Exception ex)
        {
            _log($"Power: could not delete {BoostMarkerPath} ({ex.Message}).");
        }
    }

    /// <summary>
    /// At service start: a boost the last run never undid (killed, crashed, power cut) is undone now - if the plan is
    /// still the one it set.
    /// </summary>
    public static void RestoreLeftovers(Action<string> log)
    {
        var boost = ReadBoostMarker();
        var tune = new PowerTune(() => false, () => TunnelState.Disconnected, log);
        if (boost is null)
        {
            tune.DeleteBoostMarker();
            return;
        }
        lock (tune._gate) tune.PutBackLocked(boost);
        log($"Power: a plan boost left by the last run was undone (back to {boost.FromScheme}).");
    }

    // ------------------------------------------------------------ network card power saving

    private sealed record NicEntry(string DriverKey, string Keyword, string Value);

    /// <summary>
    /// Turns off the line card's power saving and restarts it (a few seconds without network). Null when done or
    /// nothing was on; else an error code the app words: "game-running", "tunnel-up", "reboot" (written, needs a
    /// restart of the PC), or a sentence.
    /// </summary>
    public async Task<string?> TurnOffNicPowerSavingAsync()
    {
        if (_gameRunning()) return "game-running";
        if (TunnelUp()) return "tunnel-up";
        if (!await _nic.WaitAsync(0).ConfigureAwait(false)) return "A change to the network card is already running.";
        try
        {
            return await Task.Run(TurnOffCore).ConfigureAwait(false);
        }
        finally
        {
            _nic.Release();
        }
    }

    private string? TurnOffCore()
    {
        if (_disposed) return "The service is stopping.";
        var card = _lineCard();
        if (card is null || card.On.Count == 0) return null;

        var recorded = ReadNicMarker();
        var entries = new List<NicEntry>(recorded);
        foreach (var s in card.On)
        {
            // An older record keeps the player's original value: a second press must not record our "0" as theirs.
            if (!entries.Any(e => e.DriverKey == s.DriverKey && e.Keyword == s.Keyword))
                entries.Add(new NicEntry(s.DriverKey, s.Keyword, s.Present ? s.Current ?? Absent : Absent));
        }
        try
        {
            AtomicFile.WriteAllText(NicMarkerPath, string.Join('\n', entries.Select(e => $"{e.DriverKey}\t{e.Keyword}\t{e.Value}")));
        }
        catch (Exception ex)
        {
            return $"Could not write {NicMarkerPath} ({ex.Message}) - nothing was changed.";
        }
        _nicChanged = true;

        var written = 0;
        try
        {
            using var driver = Registry.LocalMachine.OpenSubKey(NicPowerSaving.ClassKey + @"\" + card.DriverKey, writable: true);
            if (driver is null) return "The network card's settings were not found.";
            foreach (var s in card.On)
            {
                Write(driver, s.Keyword, NicPowerSaving.Off);
                written++;
            }
        }
        catch (Exception ex)
        {
            _log($"Power: could not write the network card's settings ({ex.Message}).");
            if (written == 0) return "Windows would not change the network card's settings.";
        }

        _log($"Power: turned off on {card.Description}: {string.Join(", ", card.On.Select(s => $"{s.Keyword} (was {s.Current ?? "default"})"))} - restarting the card.");
        var (result, error) = DeviceRestart.Restart(card.DriverKey);
        _log($"Power: card restart: {result}{(error != 0 ? $" (error {error})" : "")}.");
        Changed?.Invoke();
        return result switch
        {
            DeviceRestart.Result.Restarted => null,
            DeviceRestart.Result.NeedsReboot => "reboot",
            _ => "reboot", // written; the driver reads it at the next start of the card
        };
    }

    /// <summary>Puts the card's settings back as they were before the app changed them, and restarts it.</summary>
    public async Task<string?> RestoreNicAsync()
    {
        if (_gameRunning()) return "game-running";
        if (TunnelUp()) return "tunnel-up";
        if (!await _nic.WaitAsync(0).ConfigureAwait(false)) return "A change to the network card is already running.";
        try
        {
            return await Task.Run(RestoreNicCore).ConfigureAwait(false);
        }
        finally
        {
            _nic.Release();
        }
    }

    private string? RestoreNicCore()
    {
        var entries = ReadNicMarker();
        string? outcome = null;
        foreach (var card in entries.GroupBy(e => e.DriverKey))
        {
            var put = new List<string>();
            try
            {
                using var driver = Registry.LocalMachine.OpenSubKey(NicPowerSaving.ClassKey + @"\" + card.Key, writable: true);
                if (driver is null)
                {
                    _log($"Power: network card {card.Key} is gone - nothing to put back.");
                    continue;
                }
                foreach (var e in card)
                {
                    if (e.Value == Absent) driver.DeleteValue(e.Keyword, throwOnMissingValue: false);
                    else if (NicPowerSaving.IsOffered(driver, e.Keyword, e.Value)) Write(driver, e.Keyword, e.Value);
                    else
                    {
                        _log($"Power: {e.Value} is not a value card {card.Key} offers for {e.Keyword} - left as it is.");
                        continue;
                    }
                    put.Add($"{e.Keyword}={(e.Value == Absent ? "default" : e.Value)}");
                }
            }
            catch (Exception ex)
            {
                _log($"Power: could not put back the settings of card {card.Key} ({ex.Message}).");
                return "Windows would not change the network card's settings.";
            }
            var (result, error) = DeviceRestart.Restart(card.Key);
            _log($"Power: card {card.Key} settings put back ({string.Join(", ", put)}); restart: {result}{(error != 0 ? $" (error {error})" : "")}.");
            if (result != DeviceRestart.Result.Restarted && result != DeviceRestart.Result.NotFound) outcome = "reboot";
        }
        try
        {
            File.Delete(NicMarkerPath);
        }
        catch (Exception ex)
        {
            _log($"Power: could not delete {NicMarkerPath} ({ex.Message}).");
        }
        _nicChanged = false;
        Changed?.Invoke();
        return outcome;
    }

    private bool TunnelUp() => _tunnelState() is not (TunnelState.Disconnected or TunnelState.Faulted);

    /// <summary>Writes a value in the kind the driver already uses (most use strings; a few DWORDs).</summary>
    private static void Write(RegistryKey driver, string keyword, string value)
    {
        var kind = driver.GetValueNames().Contains(keyword, StringComparer.OrdinalIgnoreCase)
            ? driver.GetValueKind(keyword)
            : RegistryValueKind.String;
        if (kind == RegistryValueKind.DWord && int.TryParse(value, out var number)) driver.SetValue(keyword, number, RegistryValueKind.DWord);
        else driver.SetValue(keyword, value, RegistryValueKind.String);
    }

    private static List<NicEntry> ReadNicMarker()
    {
        try
        {
            if (!File.Exists(NicMarkerPath)) return [];
            return [.. File.ReadAllLines(NicMarkerPath)
                .Select(l => l.Split('\t'))
                // Checked as untrusted: only a card key, a keyword on the list and a short plain value are ever written.
                .Where(f => f.Length == 3 && f[0].Length == 4 && f[0].All(char.IsAsciiDigit) && NicPowerSaving.IsKnown(f[1])
                            && f[2].Length is > 0 and <= 8 && (f[2] == Absent || f[2].All(char.IsAsciiLetterOrDigit)))
                .Select(f => new NicEntry(f[0], f[1], f[2]))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Dispose()
    {
        Unboost("the service is stopping");
        lock (_gate) _disposed = true;
    }
}
