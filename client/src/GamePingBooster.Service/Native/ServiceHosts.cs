using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Which Windows services run in which process - what Task Manager shows under a "Service Host" line. A download by
/// svchost means nothing to a player; one by the svchost hosting DoSvc is Windows Update.
/// </summary>
internal static partial class ServiceHosts
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceActive = 1;
    private const int ErrorMoreData = 234;

    // ENUM_SERVICE_STATUS_PROCESSW: two string pointers, then SERVICE_STATUS_PROCESS (nine DWORDs, the pid eighth),
    // padded to pointer alignment.
    private static readonly int EntrySize = (2 * IntPtr.Size + 9 * 4 + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
    private static readonly int PidOffset = 2 * IntPtr.Size + 7 * 4;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(nint manager, int infoLevel, uint serviceType, uint serviceState,
        nint services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenServiceW(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(nint service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, nint tagId, string? dependencies, string? startName,
        string? password, string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    /// <summary>One running service: its short name (wuauserv, DoSvc...) and the name Services shows.</summary>
    internal readonly record struct Hosted(string Name, string DisplayName);

    /// <summary>Running services by process id. Empty when unreadable.</summary>
    public static Dictionary<uint, List<Hosted>> ByPid()
    {
        var result = new Dictionary<uint, List<Hosted>>();
        var manager = OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (manager == 0) return result;
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive, 0, 0, out var needed, out _,
                ref resume, null);
            if (Marshal.GetLastPInvokeError() != ErrorMoreData || needed == 0) return result;

            var size = needed + 4096; // services may start between the two calls
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive, buffer, size,
                        out _, out var count, ref resume, null))
                    return result;
                for (var i = 0; i < count; i++)
                {
                    var at = buffer + i * EntrySize;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(at));
                    var display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(at + IntPtr.Size));
                    var pid = (uint)Marshal.ReadInt32(at + PidOffset);
                    if (pid == 0 || string.IsNullOrEmpty(name)) continue;
                    if (!result.TryGetValue(pid, out var list)) result[pid] = list = [];
                    list.Add(new Hosted(name, string.IsNullOrEmpty(display) ? name : display));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            // Nothing named is better than nothing at all.
        }
        finally
        {
            CloseServiceHandle(manager);
        }
        return result;
    }

    /// <summary>
    /// Sets a service's start type (2 automatic, 3 manual, 4 disabled), leaving everything else - the delayed-start
    /// flag of an automatic service included - as it is. Throws Win32Exception when Windows refuses.
    /// </summary>
    public static void SetStartType(string name, uint startType)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var service = OpenServiceW(manager, name, ServiceChangeConfig);
            if (service == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (!ChangeServiceConfigW(service, ServiceNoChange, startType, ServiceNoChange,
                        null, null, 0, null, null, null, null))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
}
