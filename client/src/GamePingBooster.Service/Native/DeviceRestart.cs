using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Restarts one network card so its driver reads a changed Advanced setting - what Device Manager does after OK on the
/// Advanced tab (DIF_PROPERTYCHANGE / DICS_PROPCHANGE through SetupAPI; the same call devcon "restart" makes). Present
/// on every Windows since XP, so no PowerShell, WMI or NetAdapter module is involved.
///
/// The card is found by its driver key ("0005" under the Net class), which is what NicPowerSaving reports: a device's
/// SPDRP_DRIVER property is "{4d36e972-...}\0005".
/// </summary>
internal static partial class DeviceRestart
{
    private static readonly Guid NetClass = new("4d36e972-e325-11ce-bfc1-08002be10318");
    private const uint DigcfPresent = 0x2;
    private const uint SpdrpDriver = 0x9;
    private const uint DifPropertyChange = 0x12;
    private const uint DicsPropChange = 3;
    private const uint DicsFlagGlobal = 1;
    private const uint DiNeedRestart = 0x80;
    private const uint DiNeedReboot = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpPropChangeParams
    {
        public uint HeaderSize; // SP_CLASSINSTALL_HEADER.cbSize = 8
        public uint InstallFunction;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint SetupDiGetClassDevsW(in Guid classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(nint set, uint index, ref SpDevinfoData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryPropertyW(nint set, ref SpDevinfoData data, uint property,
        out uint regType, nint buffer, uint size, out uint required);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiSetClassInstallParamsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetClassInstallParamsW(nint set, ref SpDevinfoData data, ref SpPropChangeParams p, uint size);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiCallClassInstaller", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiCallClassInstaller(uint function, nint set, ref SpDevinfoData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstallParamsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstallParamsW(nint set, ref SpDevinfoData data, nint installParams);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    public enum Result
    {
        Restarted,
        /// <summary>The driver took the change but the card only picks it up after a reboot.</summary>
        NeedsReboot,
        NotFound,
        Failed,
    }

    /// <summary>Restarts the present network card with this driver key. Blocks while the card goes down and up (seconds).</summary>
    public static (Result Result, int Error) Restart(string driverKey)
    {
        var set = SetupDiGetClassDevsW(NetClass, null, 0, DigcfPresent);
        if (set == -1) return (Result.Failed, Marshal.GetLastPInvokeError());
        try
        {
            var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!DriverKeyOf(set, ref data).EndsWith(@"\" + driverKey, StringComparison.OrdinalIgnoreCase)) continue;

                var p = new SpPropChangeParams
                {
                    HeaderSize = 8,
                    InstallFunction = DifPropertyChange,
                    StateChange = DicsPropChange,
                    Scope = DicsFlagGlobal,
                    HwProfile = 0,
                };
                if (!SetupDiSetClassInstallParamsW(set, ref data, ref p, (uint)Marshal.SizeOf<SpPropChangeParams>()))
                    return (Result.Failed, Marshal.GetLastPInvokeError());
                if (!SetupDiCallClassInstaller(DifPropertyChange, set, ref data))
                    return (Result.Failed, Marshal.GetLastPInvokeError());
                return (NeedsReboot(set, ref data) ? Result.NeedsReboot : Result.Restarted, 0);
            }
            return (Result.NotFound, 0);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string DriverKeyOf(nint set, ref SpDevinfoData data)
    {
        const int Size = 512;
        var buffer = Marshal.AllocHGlobal(Size);
        try
        {
            return SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpDriver, out _, buffer, Size, out _)
                ? Marshal.PtrToStringUni(buffer) ?? ""
                : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// SP_DEVINSTALL_PARAMS_W.Flags after the call: the class installer sets DI_NEEDRESTART/DI_NEEDREBOOT when the card
    /// could not be stopped. The struct is 584 bytes on x64 (Flags at offset 4); a wrong size reads as "restarted".
    /// </summary>
    private static bool NeedsReboot(nint set, ref SpDevinfoData data)
    {
        var size = IntPtr.Size == 8 ? 584 : 556;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, size);
            if (!SetupDiGetDeviceInstallParamsW(set, ref data, buffer)) return false;
            var flags = (uint)Marshal.ReadInt32(buffer, 4);
            return (flags & (DiNeedRestart | DiNeedReboot)) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
