namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Empties PUBG's network cache (TslGame\Saved\CohCache) when the game starts. A run that began
/// while the line was broken (a VPN or WARP in the way, a relay dying mid-lobby) can leave a
/// half-written cache behind, and the game then sits on a black screen on every later start even
/// though the network is healthy - deleting the folder is what got such players in again.
///
/// Only that one folder: settings (Saved\Config), shader caches and replays are left alone, and
/// nothing is read or written inside the game's process. The service runs as LocalSystem, so
/// %LOCALAPPDATA% is not the player's; every profile under the Users root is checked instead.
/// </summary>
internal static class GameCacheCleaner
{
    private const string CacheRelativePath = @"AppData\Local\TslGame\Saved\CohCache";

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
