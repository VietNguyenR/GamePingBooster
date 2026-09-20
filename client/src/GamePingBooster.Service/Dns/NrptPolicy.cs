using Microsoft.Win32;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Points Windows at this service's resolver for a few name suffixes, and nothing else.
///
/// The Name Resolution Policy Table is how Windows scopes DNS by name: a rule says "for anything
/// under .steamcommunity.com, ask these servers", and every other name on the machine keeps
/// resolving exactly as it did. It is the mechanism split-DNS VPNs use, and it is chosen here over
/// the obvious alternative - overwriting the adapter's DNS servers - because the blast radius is
/// the difference between five names and the whole machine. If this service stops, a stale rule
/// breaks Steam; a stale adapter setting breaks everything.
///
/// Written straight to the registry rather than through PowerShell's <c>Add-DnsClientNrptRule</c>,
/// which writes to this same key: the service is a Native AOT binary and starting powershell.exe
/// from it, as SYSTEM, on a connect path measured in milliseconds, is the netsh mistake again.
///
/// Rules are keyed by a GUID this service owns, so an interrupted run leaves something recognisable
/// to remove and nothing belonging to anyone else is ever touched. <see cref="Remove"/> is safe to
/// call when nothing is installed, which is what makes it safe to call from a crash path.
/// </summary>
internal static class NrptPolicy
{
    private const string PolicyPath = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig";

    /// <summary>
    /// The rule name prefix. Fixed and recognisable on purpose: somebody reading this key on a
    /// player's machine at two in the morning should be able to tell at a glance who put it there.
    /// </summary>
    private const string RulePrefix = "GamePingBooster-Steam-";

    /// <summary>DNS_POLICY_CONFIG version. 1 is what Windows writes and what it reads.</summary>
    private const int Version = 1;

    /// <summary>
    /// ConfigOptions bit 3 (0x8): the rule names its own DNS servers in GenericDNSServers. Without
    /// it the rule exists and steers nothing, which looks exactly like the feature working.
    /// </summary>
    private const int ConfigUseGenericDnsServers = 0x8;

    /// <summary>
    /// Installs one rule per suffix. Returns the number written.
    ///
    /// One rule per suffix rather than one rule with several namespaces: Windows accepts both, but
    /// a per-suffix rule can be removed on its own, and the failure mode of a partial write is then
    /// "one name is not covered" instead of "the whole table is malformed".
    /// </summary>
    public static int Install(IReadOnlyList<string> namespaces, string resolverAddress, Action<string> log)
    {
        // Any leftovers go first. A rule from a previous run points at a resolver that is not
        // listening, and leaving it in place while adding another would make Windows try the dead
        // one first.
        Remove(log);

        using var policy = Registry.LocalMachine.CreateSubKey(PolicyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKLM\\{PolicyPath}.");

        var written = 0;

        for (var i = 0; i < namespaces.Count; i++)
        {
            var name = RulePrefix + i.ToString("00");

            using var rule = policy.CreateSubKey(name, writable: true);
            if (rule is null)
            {
                log($"Could not create the NRPT rule {name}.");
                continue;
            }

            rule.SetValue("Version", Version, RegistryValueKind.DWord);

            // REG_MULTI_SZ even for one entry - this is the type Windows reads, and a REG_SZ here
            // is ignored silently rather than rejected.
            rule.SetValue("Name", new[] { namespaces[i] }, RegistryValueKind.MultiString);
            rule.SetValue("GenericDNSServers", resolverAddress, RegistryValueKind.String);
            rule.SetValue("ConfigOptions", ConfigUseGenericDnsServers, RegistryValueKind.DWord);

            written++;
        }

        // The DNS client keeps answers from before the rule existed, and on this line the cached
        // answer for a scoped name is 127.0.0.1 with whatever TTL the ISP chose. Without this the
        // feature appears not to work until the cache expires.
        DnsCache.Flush();

        log($"NRPT: {written} rule(s) point {string.Join(", ", namespaces)} at {resolverAddress}.");
        return written;
    }

    /// <summary>
    /// Removes every rule this service owns. Never throws: it is called from shutdown and from the
    /// rollback path of a failed enable, and neither can afford to fail.
    /// </summary>
    public static int Remove(Action<string> log)
    {
        var removed = 0;

        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(PolicyPath, writable: true);
            if (policy is null) return 0;

            foreach (var name in policy.GetSubKeyNames())
            {
                if (!name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    policy.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                    removed++;
                }
                catch (Exception ex)
                {
                    log($"Could not remove the NRPT rule {name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log($"Could not read the NRPT policy key: {ex.Message}");
            return removed;
        }

        if (removed > 0)
        {
            // The scoped names are cached against OUR resolver's answers now. Leaving them would
            // keep Steam working for a while after the feature was turned off, which sounds
            // harmless and means "off" cannot be tested.
            DnsCache.Flush();
            log($"NRPT: removed {removed} rule(s).");
        }

        return removed;
    }

    /// <summary>What is installed right now, for the log and for the status line.</summary>
    public static int Count()
    {
        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(PolicyPath);
            if (policy is null) return 0;

            return policy.GetSubKeyNames()
                .Count(n => n.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
