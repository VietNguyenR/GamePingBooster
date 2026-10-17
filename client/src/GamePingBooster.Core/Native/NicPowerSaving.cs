using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GamePingBooster.Core.Native;

/// <summary>
/// The network card's own power saving, for item 5 of the PC tune-up panel: Energy Efficient Ethernet and the vendors'
/// "green" modes, which let the card nap between packets or drop the link speed - the extra wake-up time lands on game
/// packets, and some (Intel I225/I226 with EEE, Realtek Green Ethernet) drop the link for a few seconds on certain
/// switches. In Core because the app shows what the service would change, and both must name the same settings.
///
/// Read from the card's driver key (HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}\NNNN), which users may
/// read - the same place Device Manager's Advanced tab reads. A setting counts only when it is on the list below, its
/// driver offers "0" in its own value list, and that value reads Disabled/Off (or the list is just 0 and 1): a value
/// the driver does not offer is never written.
///
/// Not here on purpose: "Allow the computer to turn off this device" (PnPCapabilities) and Selective Suspend act only
/// while the card is idle - not while a game moves packets - and Wake-on-LAN settings do nothing while awake.
/// </summary>
public static class NicPowerSaving
{
    public const string ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// The power-saving keywords of the common gaming-PC cards (Intel, Realtek/Killer), each turned off by "0".
    /// </summary>
    private static readonly string[] Keywords =
    [
        "*EEE",                     // Energy-Efficient Ethernet, the standard keyword (Realtek, Killer, newer Intel)
        "EEELinkAdvertisement",     // Intel: Energy Efficient Ethernet
        "AdvancedEEE",              // Realtek: Advanced EEE
        "EEEPlus",                  // Realtek: EEE+
        "EnableGreenEthernet",      // Realtek: Green Ethernet
        "GreenEthernet",
        "PowerSavingMode",          // Realtek: Power Saving Mode
        "GigaLite",                 // Realtek: Gigabit Lite (drops to 500 Mbps to save power)
        "ULPMode",                  // Intel: Ultra Low Power Mode
        "SipsEnabled",              // Intel: System Idle Power Saver
        "AutoPowerSaveModeEnabled", // Intel: Link Speed Battery Saver
        "DMACoalescing",            // Intel: DMA Coalescing (holds packets to batch memory writes)
    ];

    public const string Off = "0";

    /// <summary>A keyword on the list above - the only ones the service will ever write or put back.</summary>
    public static bool IsKnown(string keyword) => Keywords.Contains(keyword, StringComparer.Ordinal);

    /// <summary>One power-saving setting that is on. <see cref="Present"/> false: the driver's default is in use.</summary>
    public sealed record Setting(string DriverKey, string Keyword, string Name, string? Current, bool Present);

    /// <summary>The card a set of settings belongs to: its driver key ("0005") and what Device Manager calls it.</summary>
    public sealed record Card(string DriverKey, string Description, List<Setting> On);

    [DllImport("iphlpapi.dll", EntryPoint = "ConvertInterfaceIndexToLuid", ExactSpelling = true)]
    private static extern int ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong luid);

    [DllImport("iphlpapi.dll", EntryPoint = "ConvertInterfaceLuidToGuid", ExactSpelling = true)]
    private static extern int ConvertInterfaceLuidToGuid(ref ulong luid, out Guid guid);

    /// <summary>
    /// The card the internet line leaves by (the route to a public address, as Windows would pick it), with the
    /// power-saving settings that are on. Null when it cannot be found or is not a card with a driver key of this kind
    /// (Wintun, a VPN adapter, Wi-Fi drivers that have none of these). Never throws.
    /// </summary>
    public static Card? LineCard()
    {
        try
        {
#if DEBUG
            // Dev builds only: a card GUID in this file stands in for the line, so the panel can be driven on a PC whose
            // line card has none of these settings (a Wi-Fi PC testing a wired card). Compiled out of Release.
            var stand = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GamePingBooster", "test-line-card");
            if (File.Exists(stand) && Guid.TryParse(File.ReadAllText(stand).Trim(), out var test)) return CardFor(test);
#endif
            var index = IpHelperInterop.BestInterfaceFor(IPAddress.Parse("1.1.1.1"));
            if (index is null) return null;
            if (ConvertInterfaceIndexToLuid(index.Value, out var luid) != 0) return null;
            if (ConvertInterfaceLuidToGuid(ref luid, out var guid) != 0) return null;
            return CardFor(guid);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The card whose NetCfgInstanceId is <paramref name="adapter"/>. Null when not found or unreadable.</summary>
    public static Card? CardFor(Guid adapter)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(ClassKey);
            if (cls is null) return null;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue;
                try
                {
                    using var driver = cls.OpenSubKey(sub);
                    if (driver?.GetValue("NetCfgInstanceId") is not string id || !Guid.TryParse(id, out var g) || g != adapter) continue;
                    var description = driver.GetValue("DriverDesc") as string ?? "";
                    return new Card(sub, description, SettingsOn(driver, sub));
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // One unreadable card key does not stop the search.
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return null;
    }

    /// <summary>The listed settings of one card that are on and can be turned off with a value the driver offers.</summary>
    public static List<Setting> SettingsOn(RegistryKey driver, string driverKey)
    {
        var on = new List<Setting>();
        foreach (var keyword in Keywords)
        {
            try
            {
                using var param = driver.OpenSubKey(@"Ndi\params\" + keyword);
                if (param is null || !OffIsOffered(param)) continue;
                var raw = driver.GetValue(keyword);
                var current = raw?.ToString()?.Trim();
                var present = raw is not null;
                if (!present) current = param.GetValue("default")?.ToString()?.Trim();
                if (current is null || current == Off) continue;
                on.Add(new Setting(driverKey, keyword, NameOf(param, keyword), present ? current : null, present));
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return on;
    }

    /// <summary>The driver lists <paramref name="value"/> as a choice for <paramref name="keyword"/>.</summary>
    public static bool IsOffered(RegistryKey driver, string keyword, string value)
    {
        using var choices = driver.OpenSubKey(@"Ndi\params\" + keyword + @"\enum");
        return choices?.GetValueNames().Contains(value) == true;
    }

    /// <summary>The driver lists "0" as a choice and it means off.</summary>
    private static bool OffIsOffered(RegistryKey param)
    {
        using var choices = param.OpenSubKey("enum");
        if (choices is null) return false;
        var names = choices.GetValueNames();
        if (!names.Contains(Off)) return false;
        if (names.Length == 2 && names.Contains("1")) return true;
        var label = choices.GetValue(Off)?.ToString() ?? "";
        return label.Contains("disab", StringComparison.OrdinalIgnoreCase) || label.Contains("off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What Device Manager shows for it ("Energy Efficient Ethernet"). Indirect strings keep their fallback text.</summary>
    private static string NameOf(RegistryKey param, string keyword)
    {
        var desc = param.GetValue("ParamDesc")?.ToString();
        if (string.IsNullOrWhiteSpace(desc)) return keyword.TrimStart('*');
        if (desc.StartsWith('@') && desc.LastIndexOf(';') is var semi and > 0) desc = desc[(semi + 1)..];
        return desc.Trim();
    }
}
