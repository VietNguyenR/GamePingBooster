using System.Runtime.InteropServices;

namespace GamePingBooster.App.Services;

/// <summary>Wi-Fi band of the access point the PC is connected to.</summary>
public enum WifiBand
{
    Unknown,
    GHz24,
    GHz5,
    GHz6,
}

/// <summary>What Windows says about one Wi-Fi adapter's current connection.</summary>
public sealed record WifiLink(int SignalPercent, WifiBand Band);

/// <summary>
/// Reads the current Wi-Fi connection from wlanapi.dll, for the "Kết nối mạng / Wi-Fi" line of the checklist.
///
/// Signal is wlanSignalQuality, the 0-100 figure Windows itself draws as bars, so the number the player sees here is
/// the one they can check against the tray icon. Band comes from the connected access point's centre frequency in the
/// BSS list, because a channel number alone cannot tell 6 GHz from 2.4/5 GHz; the channel number is only the fallback.
///
/// Read with offsets into the native buffers instead of marshalled structs: everything here is a fixed layout from
/// wlantypes.h/wlanapi.h, and it stays trim- and AOT-safe without unsafe code.
/// </summary>
public static class WlanInfo
{
    private const string Dll = "wlanapi.dll";

    private const int ClientVersion = 2; // Vista and later
    private const int OpcodeCurrentConnection = 7; // wlan_intf_opcode_current_connection
    private const int OpcodeChannelNumber = 8; // wlan_intf_opcode_channel_number
    private const int StateConnected = 1; // wlan_interface_state_connected

    // WLAN_CONNECTION_ATTRIBUTES: isState(4) mode(4) strProfileName(512) then WLAN_ASSOCIATION_ATTRIBUTES:
    // DOT11_SSID(36) bssType(4) bssid(6)+pad(2) phyType(4) phyIndex(4) signalQuality(4) rx(4) tx(4).
    private const int ConnSsid = 520;
    private const int ConnBssType = 556;
    private const int ConnBssid = 560;
    private const int ConnSignal = 576;

    // WLAN_BSS_LIST: dwTotalSize(4) dwNumberOfItems(4), then WLAN_BSS_ENTRY[] of 360 bytes each.
    private const int ListItems = 4;
    private const int ListFirst = 8;
    private const int EntrySize = 360;
    private const int EntryBssid = 40;
    private const int EntryFrequencyKhz = 92;

    // DllImport rather than LibraryImport, as elsewhere in this app: see Loc.cs. Every argument is blittable.
    [DllImport(Dll, ExactSpelling = true)]
    private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr handle);

    [DllImport(Dll, ExactSpelling = true)]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport(Dll, ExactSpelling = true)]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport(Dll, ExactSpelling = true)]
    private static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opcode, IntPtr reserved,
        out int dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport(Dll, ExactSpelling = true)]
    private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid interfaceGuid, IntPtr ssid, int bssType,
        int securityEnabled, IntPtr reserved, out IntPtr bssList);

    /// <summary>
    /// The adapter's current Wi-Fi link, or null when it is not connected or Windows will not say (WLAN AutoConfig
    /// stopped, no wlanapi.dll on a Server install). Never throws.
    /// </summary>
    public static WifiLink? Query(Guid adapter)
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr conn = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle) != 0) return null;
            if (WlanQueryInterface(handle, ref adapter, OpcodeCurrentConnection, IntPtr.Zero, out var size, out conn,
                    IntPtr.Zero) != 0 || size < ConnSignal + 4)
                return null;
            if (Marshal.ReadInt32(conn) != StateConnected) return null;

            var signal = Math.Clamp(Marshal.ReadInt32(conn, ConnSignal), 0, 100);
            var band = BandFromBssList(handle, adapter, conn) ?? BandFromChannel(handle, adapter);
            return new WifiLink(signal, band);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (conn != IntPtr.Zero) WlanFreeMemory(conn);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>The connected BSSID's centre frequency, from the cached scan list for its SSID.</summary>
    private static WifiBand? BandFromBssList(IntPtr handle, Guid adapter, IntPtr conn)
    {
        var bssid = new byte[6];
        Marshal.Copy(conn + ConnBssid, bssid, 0, 6);
        var bssType = Marshal.ReadInt32(conn, ConnBssType);

        IntPtr list = IntPtr.Zero;
        try
        {
            // The SSID is passed straight out of the connection buffer, which stays valid until it is freed.
            if (WlanGetNetworkBssList(handle, ref adapter, conn + ConnSsid, bssType, 0, IntPtr.Zero, out list) != 0)
                return null;

            var count = Marshal.ReadInt32(list, ListItems);
            var entryBssid = new byte[6];
            for (var i = 0; i < count; i++)
            {
                var entry = list + ListFirst + i * EntrySize;
                Marshal.Copy(entry + EntryBssid, entryBssid, 0, 6);
                if (!entryBssid.AsSpan().SequenceEqual(bssid)) continue;

                var khz = (uint)Marshal.ReadInt32(entry, EntryFrequencyKhz);
                return khz switch
                {
                    0 => null,
                    < 3_000_000 => WifiBand.GHz24,
                    < 5_925_000 => WifiBand.GHz5,
                    _ => WifiBand.GHz6,
                };
            }
            return null;
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
        }
    }

    /// <summary>Channels 1-14 are 2.4 GHz; anything above is taken as 5 GHz (6 GHz reuses low numbers, hence last resort).</summary>
    private static WifiBand BandFromChannel(IntPtr handle, Guid adapter)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            if (WlanQueryInterface(handle, ref adapter, OpcodeChannelNumber, IntPtr.Zero, out var size, out data,
                    IntPtr.Zero) != 0 || size < 4)
                return WifiBand.Unknown;
            var channel = Marshal.ReadInt32(data);
            return channel switch
            {
                <= 0 => WifiBand.Unknown,
                <= 14 => WifiBand.GHz24,
                _ => WifiBand.GHz5,
            };
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
        }
    }
}
