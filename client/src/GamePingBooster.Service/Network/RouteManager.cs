using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace GamePingBooster.Service.Network;

/// <summary>
/// Owns the Windows routing table: pushes the game's IP ranges into the virtual adapter and
/// pulls them back out afterwards.
///
/// Why netsh instead of P/Invoke into CreateIpForwardEntry2: every route is added with
/// <c>store=active</c>, meaning it lives in RAM and disappears on reboot. For software that
/// touches a user's network that is a feature, not a limitation - after a hang or a BSOD the
/// machine comes back with a clean routing table.
///
/// The price is netsh's error reporting: it prints failures on stdout in the user's display
/// language and only surfaces them through the process exit code, which in <c>-f</c> script mode
/// reflects the last command alone. So anything whose failure matters runs through
/// <see cref="RunNetsh"/>, one command per process, and the adapter address is read back through
/// NetworkInformation afterwards rather than trusted.
/// </summary>
internal sealed class RouteManager
{
    private readonly List<string> _installedPrefixes = [];
    private string? _pinnedRelayPrefix;

    // The interface the pin was made through. Deleting a route REQUIRES naming its interface, and
    // the physical adapter can change between pinning and unpinning (Wi-Fi to Ethernet), so the
    // index has to be remembered rather than looked up again at delete time.
    private uint _pinnedRelayInterface;

    /// <summary>Number of routes currently installed.</summary>
    public int ActiveRouteCount => _installedPrefixes.Count + (_pinnedRelayPrefix is null ? 0 : 1);

    /// <summary>
    /// Game routes only, excluding the pinned relay route. The reconnect path uses this to tell
    /// whether it pulled the game off the tunnel and therefore owes it a reinstall on success.
    /// </summary>
    public int ActiveGameRouteCount => _installedPrefixes.Count;

    /// <summary>
    /// Assigns the inner IP and MTU to the virtual adapter. Call this after the relay has
    /// handed out an address during the handshake.
    /// </summary>
    public void ConfigureAdapter(uint tunInterfaceIndex, IPAddress innerIp, int prefixLength, int mtu)
    {
        var mask = PrefixLengthToMask(prefixLength);

        // Mind the parameter names, they are not consistent across netsh commands:
        //   set address       takes name=      ("Interface name or index")
        //   set subinterface  takes interface=
        //   set interface     takes interface=
        // Writing interface= on set address is a syntax error, and netsh only reports it on
        // stdout - which is why each of these runs as its own process with its own exit code.
        RunNetsh($"interface ipv4 set address name={tunInterfaceIndex} source=static address={innerIp} mask={mask} store=active");
        RunNetsh($"interface ipv4 set subinterface interface={tunInterfaceIndex} mtu={mtu} store=active");
        // Do not let Windows register DNS for this adapter; it is not a real network card.
        RunNetsh($"interface ipv4 set interface interface={tunInterfaceIndex} dadtransmits=0 store=active");

        WaitForAddress(tunInterfaceIndex, innerIp);
    }

    /// <summary>
    /// Confirms the address really landed on the adapter, reading it back through
    /// NetworkInformation rather than parsing netsh output (which is localised).
    ///
    /// netsh can report success while the address is not usable yet, and the failure that follows
    /// is silent: routes install fine, packets go nowhere, and nothing logs an error. Better to
    /// fail here, loudly, than to hand the user a tunnel that looks connected and does nothing.
    /// </summary>
    private static void WaitForAddress(uint tunInterfaceIndex, IPAddress expected, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }

                try
                {
                    if (props.GetIPv4Properties()?.Index != (int)tunInterfaceIndex) continue;
                }
                catch (NetworkInformationException) { continue; }

                if (props.UnicastAddresses.Any(a => a.Address.Equals(expected))) return;
            }
            Thread.Sleep(100);
        } while (Environment.TickCount64 < deadline);

        throw new InvalidOperationException(
            $"The virtual adapter (interface {tunInterfaceIndex}) still does not carry {expected} " +
            $"after {timeoutMs} ms. netsh reported success, so the adapter is most likely in a bad " +
            "state - disconnect, then connect again.");
    }

    /// <summary>
    /// Pins a /32 route for the relay itself through the PHYSICAL network adapter.
    ///
    /// This is the single most important step of the whole mechanism: without it, if a routed
    /// range happens to contain the relay's own address, packets destined for the relay get
    /// pushed back into the tunnel - an infinite loop that takes the machine offline. Pin first,
    /// always.
    /// </summary>
    public void PinRelayRoute(IPAddress relayIp)
    {
        var (physIndex, gateway) = GetDefaultRoute()
            ?? throw new InvalidOperationException(
                "No network adapter with a default gateway was found - is the machine offline?");

        var prefix = $"{relayIp}/32";

        // Failing over to another relay pins a different address, and _pinnedRelayPrefix only
        // holds one. Overwriting it without deleting first would strand the previous /32 in the
        // routing table forever: RemoveAll can only delete the prefix it still remembers, so the
        // old entry would outlive the service.
        if (_pinnedRelayPrefix is not null && _pinnedRelayPrefix != prefix)
        {
            DeleteRoute(_pinnedRelayPrefix, _pinnedRelayInterface);
            _pinnedRelayPrefix = null;
        }

        // Always delete before adding. This route goes through the PHYSICAL adapter, so unlike
        // the game routes it does not disappear when the virtual adapter goes away - a service
        // that was killed rather than stopped cleanly leaves it behind, and then `add` fails.
        // We cannot simply ignore that failure by matching netsh's message, because the message
        // is localised: an English Windows says "The object already exists" and a Vietnamese one
        // says something else entirely.
        DeleteRoute(prefix, physIndex);

        RunNetsh($"interface ipv4 add route prefix={prefix} interface={physIndex} nexthop={gateway} metric=1 store=active");
        _pinnedRelayPrefix = prefix;
        _pinnedRelayInterface = physIndex;
    }

    /// <summary>
    /// Installs routes for the game's CIDR list, pointing at the virtual adapter.
    /// A low metric so they win against the physical adapter's default route.
    ///
    /// The routes are <b>on-link</b> - no nexthop is given. This matters: Wintun presents an
    /// NDIS layer-3 medium with no link layer, so there is nothing to resolve a nexthop address
    /// against. Naming a gateway (even one inside the tunnel subnet) leaves Windows waiting on a
    /// neighbour entry that can never appear, and it silently drops the packets instead of
    /// handing them to the adapter. This is also how WireGuard configures its own routes.
    /// </summary>
    public void InstallGameRoutes(uint tunInterfaceIndex, IEnumerable<string> cidrs)
    {
        var fresh = new List<string>();
        foreach (var cidr in cidrs)
        {
            if (!IsValidIPv4Cidr(cidr)) continue;
            if (_installedPrefixes.Contains(cidr)) continue;
            fresh.Add(cidr);
        }
        if (fresh.Count == 0) return;

        // Same reasoning as PinRelayRoute: clear any leftover entry first so `add` cannot fail
        // on a duplicate. Two netsh invocations for the whole batch, not two per route.
        RunNetshScript(
            fresh.Select(cidr =>
                $"interface ipv4 delete route prefix={cidr} interface={tunInterfaceIndex} store=active").ToList(),
            ignoreErrors: true);

        // Record them before the adds run, not after: if one fails partway some routes are
        // already in the table, and teardown must still know to remove them.
        _installedPrefixes.AddRange(fresh);

        // One process per route so each exit code is attributable. A real PUBG profile is a few
        // dozen prefixes, so this costs a second or two - once, when the game starts. Worth it:
        // a route that silently fails to install looks exactly like a relay that is down.
        foreach (var cidr in fresh)
        {
            RunNetsh($"interface ipv4 add route prefix={cidr} interface={tunInterfaceIndex} metric=1 store=active");
        }
    }

    /// <summary>Removes every game route, leaving the pinned relay route in place.</summary>
    public void RemoveGameRoutes(uint tunInterfaceIndex)
    {
        if (_installedPrefixes.Count == 0) return;

        var commands = _installedPrefixes
            .Select(cidr => $"interface ipv4 delete route prefix={cidr} interface={tunInterfaceIndex} store=active")
            .ToList();
        RunNetshScript(commands, ignoreErrors: true);
        _installedPrefixes.Clear();
    }

    /// <summary>Removes everything this RouteManager installed. Always call this on teardown.</summary>
    public void RemoveAll(uint tunInterfaceIndex)
    {
        RemoveGameRoutes(tunInterfaceIndex);

        if (_pinnedRelayPrefix is not null)
        {
            DeleteRoute(_pinnedRelayPrefix, _pinnedRelayInterface);
            _pinnedRelayPrefix = null;
        }
    }

    /// <summary>Finds the interface index and gateway of the current route to the internet.</summary>
    public static (uint InterfaceIndex, IPAddress Gateway)? GetDefaultRoute()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            // Skip our own virtual adapter and other virtual ones (VMware, Hyper-V).
            if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;

            var props = nic.GetIPProperties();
            var gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gateway is null) continue;

            try
            {
                return ((uint)props.GetIPv4Properties().Index, gateway);
            }
            catch (NetworkInformationException)
            {
                // Adapter has no IPv4 - skip it.
            }
        }
        return null;
    }

    /// <summary>
    /// Deletes one route. Every deletion goes through here for one reason: netsh REQUIRES
    /// interface= on `delete route`, and when it is missing netsh prints its usage text and
    /// <b>exits with code 0</b>. The command does nothing, the exit code says success, and the
    /// route silently stays in the table - which is how the pinned relay route survived every
    /// disconnect for weeks. Taking the interface as a parameter makes that mistake impossible to
    /// write rather than merely unlikely.
    /// </summary>
    private static void DeleteRoute(string prefix, uint interfaceIndex)
        => RunNetsh($"interface ipv4 delete route prefix={prefix} interface={interfaceIndex} store=active",
            ignoreErrors: true);

    private static bool IsValidIPv4Cidr(string cidr)
    {
        var parts = cidr.Split('/');
        return parts.Length == 2
               && IPAddress.TryParse(parts[0], out var ip)
               && ip.AddressFamily == AddressFamily.InterNetwork
               && int.TryParse(parts[1], out var bits)
               && bits is >= 0 and <= 32;
    }

    private static string PrefixLengthToMask(int prefixLength)
    {
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return $"{(mask >> 24) & 0xff}.{(mask >> 16) & 0xff}.{(mask >> 8) & 0xff}.{mask & 0xff}";
    }

    /// <summary>
    /// Runs a single netsh command in its own process and checks its exit code.
    ///
    /// Use this for anything whose failure matters. netsh reports a bad command on stdout in the
    /// user's display language, so the message cannot be matched on - the exit code is the only
    /// locale-independent signal, and it is only trustworthy one command at a time.
    /// </summary>
    private static void RunNetsh(string command, bool ignoreErrors = false)
        => RunNetshCore("netsh.exe", command, [command], ignoreErrors);

    /// <summary>
    /// Runs several netsh commands in one process via a script file (netsh -f).
    ///
    /// Cheaper than one process per command, but the exit code reflects only the last command:
    /// an earlier failure is invisible here. Only use this where failures are expected and
    /// ignored, or where a separate read-back confirms the result.
    /// </summary>
    private static void RunNetshScript(IReadOnlyCollection<string> commands, bool ignoreErrors = false)
    {
        if (commands.Count == 0) return;

        var scriptPath = Path.Combine(Path.GetTempPath(), $"gpb-{Guid.NewGuid():N}.netsh");
        try
        {
            var sb = new StringBuilder();
            foreach (var c in commands) sb.AppendLine(c);
            File.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);

            RunNetshCore("netsh.exe", $"-f \"{scriptPath}\"", commands, ignoreErrors);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (IOException) { /* temp file, ignore */ }
        }
    }

    private static void RunNetshCore(
        string fileName, string arguments, IReadOnlyCollection<string> commands, bool ignoreErrors)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start netsh.exe");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15_000);

        if (ignoreErrors) return;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"netsh exited with {proc.ExitCode}. Commands: {string.Join(" | ", commands)}. " +
                $"Output: {(stdout + stderr).Trim()}");
        }

        // The exit code alone is NOT enough. A command with a missing or misspelled parameter
        // makes netsh print its usage block and exit 0 - so a route command that did nothing at
        // all looks exactly like one that worked. These commands are silent when they succeed, so
        // any output at all means something went wrong. We still do not read WHAT it says: the
        // text is localised, only its presence is not.
        var noise = (stdout + stderr).Trim();
        if (noise.Length > 0)
        {
            throw new InvalidOperationException(
                $"netsh exited 0 but printed output, which means it did not run the command. " +
                $"Commands: {string.Join(" | ", commands)}. Output: {noise}");
        }
    }
}
