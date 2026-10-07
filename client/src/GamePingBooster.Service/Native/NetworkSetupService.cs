using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Makes sure the Windows "Network Setup Service" (NetSetupSvc) can run.
///
/// Windows finishes configuring every new network adapter through this service. With it Disabled - which
/// "optimiser" tools and trimmed Windows builds do - a new Wintun device is created, stops with problem
/// 0x38 (CM_PROB_NEED_CLASS_CONFIG) and WintunCreateAdapter fails with ERROR_DEVICE_NOT_READY (4319), every
/// time, however often it is retried or reinstalled. Seen on a customer PC on 2026-10-07; setting the service
/// to Manual and starting it fixed it with no restart.
/// </summary>
internal static partial class NetworkSetupService
{
    private const string ServiceName = "NetSetupSvc";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceDemandStart = 3;

    /// <summary>
    /// Returns true when the service was Disabled or stopped and this call changed something, so the caller
    /// should try creating the adapter again. Never throws: a failure is logged and reported as false.
    /// </summary>
    public static bool EnsureRunnable(Action<string>? log)
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            var changed = false;

            if (service.StartType == ServiceStartMode.Disabled)
            {
                SetManualStart();
                log?.Invoke($"The Windows service '{ServiceName}' was Disabled, which stops any virtual adapter " +
                            "from being set up. Set it to Manual.");
                changed = true;
            }

            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                log?.Invoke($"Started the Windows service '{ServiceName}'.");
                changed = true;
            }
            return changed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not check or repair the Windows service '{ServiceName}': {ex.Message}");
            return false;
        }
    }

    private static void SetManualStart()
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == nint.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var service = OpenServiceW(manager, ServiceName, ServiceChangeConfig);
            if (service == nint.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (!ChangeServiceConfigW(service, ServiceNoChange, ServiceDemandStart, ServiceNoChange,
                        null, null, nint.Zero, null, null, null, null))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

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
}
