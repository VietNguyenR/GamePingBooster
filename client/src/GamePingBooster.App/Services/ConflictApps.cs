using System.Text.Json;

namespace GamePingBooster.App.Services;

/// <summary>One booster or VPN the "Tối ưu máy" panel can name (item 7, VpnCheck).</summary>
/// <param name="ProcessNames">booster: the app's own process names, as Process.ProcessName (no ".exe").</param>
/// <param name="AdapterWords">vpn: lowercase words looked for in an adapter's name and description.</param>
public sealed record ConflictApp(string Code, string Name, bool IsVpn, IReadOnlyList<string> ProcessNames, IReadOnlyList<string> AdapterWords);

/// <summary>
/// The boosters and VPNs item 7 knows: the list built in here, changed by the licence server's (GET /app/conflict-apps)
/// so a new program needs no release.
///
/// A server row adds a code, or replaces the built-in entry with the same code; a row switched off removes that code,
/// built-in or not. The server's last answer is kept in %LOCALAPPDATA% and used from the next start, so a PC offline,
/// or a server down, keeps the list it last had - and a fresh install with no answer yet has the built-in one.
///
/// Asked once per app run, the first time the service's status names the licence server. No sign-in is needed.
/// </summary>
public static class ConflictApps
{
    /// <summary>Seen on real installs (owner's PC, 2026-10-20). See VpnCheck for why the process and not the driver.</summary>
    public static readonly IReadOnlyList<ConflictApp> BuiltIn =
    [
        new("gearup", "GearUP Booster", false, ["gearup_booster"], []),
        new("lagofast", "LagoFast", false, ["LagoFast"], []),
        new("exitlag", "ExitLag", false, ["ExitLag"], []),
        new("noping", "NoPing", false, ["NoPing"], []),
        new("wtfast", "WTFast", false, ["WTFast"], []),
        new("warp", "Cloudflare WARP", true, [], ["warp", "cloudflare"]),
        new("radmin", "Radmin VPN", true, [], ["radmin"]),
        new("zerotier", "ZeroTier", true, [], ["zerotier"]),
        new("tailscale", "Tailscale", true, [], ["tailscale"]),
        new("hamachi", "Hamachi", true, [], ["hamachi"]),
        new("nordvpn", "NordVPN", true, [], ["nordlynx"]),
        new("letsvpn", "LetsVPN", true, [], ["letsvpn"]),
        new("wireguard", "WireGuard", true, [], ["wireguard"]),
        new("openvpn", "OpenVPN", true, [], ["openvpn", "tap-win"]),
    ];

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster", "conflict-apps.json");

    private static volatile IReadOnlyList<ConflictApp> _current = Merge(Load());
    private static int _fetched;

    /// <summary>The list in force now. Swapped whole, so a reader on another thread never sees half of it.</summary>
    public static IReadOnlyList<ConflictApp> Current => _current;

    /// <summary>Asks the server once per run; later calls do nothing. Any failure keeps the list there is.</summary>
    public static async Task RefreshOnceAsync(string? licenceUrl)
    {
        if (string.IsNullOrWhiteSpace(licenceUrl)) return;
        if (Interlocked.Exchange(ref _fetched, 1) == 1) return;
        try
        {
            using var client = new LicenceClient(licenceUrl);
            var answer = await client.FetchConflictAppsAsync(CancellationToken.None).ConfigureAwait(false);
            _current = Merge(answer.Apps);
            Save(answer);
        }
        catch (Exception)
        {
            // Offline, server down, an older server without the route (404): the built-in or last list stands.
        }
    }

    /// <summary>The built-in list with the server's rows applied, built-in order first, new codes after.</summary>
    internal static IReadOnlyList<ConflictApp> Merge(IEnumerable<ConflictAppRow>? rows)
    {
        var list = BuiltIn.ToList();
        foreach (var row in rows ?? [])
        {
            var code = row.Id.Trim().ToLowerInvariant();
            if (code.Length == 0) continue;
            var at = list.FindIndex(a => a.Code == code);
            if (!row.Active)
            {
                if (at >= 0) list.RemoveAt(at);
                continue;
            }

            var isVpn = row.Kind == "vpn";
            if (!isVpn && row.Kind != "booster") continue; // a kind this client does not know
            var app = new ConflictApp(
                code,
                row.Name.Trim().Length > 0 ? row.Name.Trim() : code,
                isVpn,
                isVpn ? [] : row.ProcessNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToList(),
                isVpn ? row.AdapterNames.Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length >= 3).ToList() : []);
            if (app.ProcessNames.Count == 0 && app.AdapterWords.Count == 0) continue;

            if (at >= 0) list[at] = app;
            else list.Add(app);
        }
        return list;
    }

    private static List<ConflictAppRow>? Load()
    {
        try
        {
            using var file = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(file, LicenceJsonContext.Default.ConflictAppsResult)?.Apps;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Save(ConflictAppsResult answer)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(answer, LicenceJsonContext.Default.ConflictAppsResult));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // In force for this run; the next start falls back to the built-in list and asks again.
        }
    }
}
