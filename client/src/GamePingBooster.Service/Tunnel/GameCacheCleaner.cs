namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Empties PUBG's network cache (TslGame\Saved\CohCache) at the start AFTER a bad run. A run that
/// began while the line was broken (a VPN or WARP in the way, a relay dying mid-lobby) can leave a
/// half-written cache behind, and the game then sits on a black screen on every later start even
/// though the network is healthy - deleting the folder is what got such players in again.
///
/// Not every start: the cache holds the 8 MB lobby bundle, which comes from PUBG's mainland .cn mirror
/// at 100-250 KB/s from Vietnam, so a cold start costs 45-70 s (measured 2026-10-07). A bad run
/// leaves a marker file; the next start clears the cache once and removes it.
///
/// Only that one folder: settings (Saved\Config), shader caches and replays are left alone, and
/// nothing is read or written inside the game's process. The service runs as LocalSystem, so
/// %LOCALAPPDATA% is not the player's; every profile under the Users root is checked instead.
/// </summary>
internal static class GameCacheCleaner
{
    private const string CacheRelativePath = @"AppData\Local\TslGame\Saved\CohCache";

    private static string MarkerPath => Path.Combine(ServiceConfig.DefaultDirectory, "pubg-cache-dirty");

    /// <summary>Notes that the run just made is suspect; the next start clears the cache. Never throws.</summary>
    public static void MarkDirty(string reason)
    {
        try
        {
            Directory.CreateDirectory(ServiceConfig.DefaultDirectory);
            File.WriteAllText(MarkerPath, $"{DateTimeOffset.Now:O} {reason}");
        }
        catch (Exception) { /* a marker that cannot be written only costs one black screen */ }
    }

    /// <summary>Clears the cache when the last run was marked bad; null when it was not.</summary>
    public static string? CleanIfDirty(string? usersRoot = null)
    {
        string reason;
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            reason = File.ReadAllText(MarkerPath).Trim();
        }
        catch (Exception) { return null; }

        var result = Clean(usersRoot);
        try { File.Delete(MarkerPath); } catch (Exception) { }
        return $"{result} The last run was marked bad: {reason}";
    }

    /// <summary>Deletes the cache for every user profile; returns a one-line log summary.</summary>
    public static string Clean(string? usersRoot = null)
    {
        usersRoot ??= Path.Combine(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\", "Users");

        var cleared = 0;
        var failed = 0;
        long bytes = 0;
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(usersRoot))
            {
                var cache = Path.Combine(profile, CacheRelativePath);
                if (!Directory.Exists(cache)) continue;
                try
                {
                    foreach (var file in new DirectoryInfo(cache).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try { bytes += file.Length; file.Delete(); }
                        catch (Exception) { failed++; } // locked by a running game: leave it
                    }
                    foreach (var dir in Directory.EnumerateDirectories(cache, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
                    {
                        try { Directory.Delete(dir, false); } catch (Exception) { /* not empty */ }
                    }
                    cleared++;
                }
                catch (Exception) { failed++; }
            }
        }
        catch (Exception ex)
        {
            return $"PUBG cache: could not look through the user profiles ({ex.GetType().Name}).";
        }

        if (cleared == 0 && failed == 0) return "PUBG cache: nothing to clear.";
        return $"PUBG cache (CohCache) cleared for {cleared} profile(s), {bytes / 1024 / 1024} MiB" +
               (failed > 0 ? $", {failed} item(s) in use and kept." : ".");
    }
}
