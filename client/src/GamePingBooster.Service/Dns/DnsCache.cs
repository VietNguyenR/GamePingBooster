using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Empties the Windows DNS client cache - what <c>ipconfig /flushdns</c> does, without starting
/// ipconfig.exe.
///
/// Needed on both edges of this feature, and for the same reason each time: the cache outlives the
/// policy. Turning the feature ON with a poisoned answer still cached means Steam keeps failing for
/// as long as the ISP's TTL says to keep it, and the player concludes the feature does nothing.
/// Turning it OFF without flushing leaves Steam working, so nobody can tell whether "off" works.
///
/// DnsFlushResolverCache is not in the SDK headers, but it is a documented-by-use export that
/// ipconfig itself calls and has been stable across every Windows release this product supports.
/// It is also the whole reason this is a file and not a line: if it ever disappears, the failure
/// belongs somewhere a person can read, not inside a try/catch in the middle of the policy code.
/// </summary>
internal static partial class DnsCache
{
    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static partial int DnsFlushResolverCache();

    /// <summary>True when the cache was emptied. Never throws - a stale cache is not worth a failed connect.</summary>
    public static bool Flush()
    {
        try
        {
            return DnsFlushResolverCache() != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
