using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// P/Invoke into wintun.dll (WireGuard's TUN driver, the officially signed build from wintun.net).
///
/// Three things matter when using this library:
///  1. The process calling <see cref="WintunCreateAdapter"/> must run as <b>LocalSystem</b>.
///     Administrator is NOT enough - this is why the engine lives in a Windows Service rather
///     than in the UI application.
///  2. The .sys driver is embedded inside the DLL; the first CreateAdapter call installs it.
///     No .inf file, no pnputil.
///  3. When the process dies the adapter disappears, and every route pointing at it goes with
///     it. That is an important safety brake: if the app crashes, the user's network heals itself.
/// </summary>
internal static partial class WintunInterop
{
    private const string Dll = "wintun.dll";

    /// <summary>Ring buffer capacity; must be a power of two in [128KB, 64MB].</summary>
    internal const uint MinRingCapacity = 0x20000;   // 128 KB
    internal const uint MaxRingCapacity = 0x4000000; // 64 MB

    internal const int ErrorNoMoreItems = 259;
    // SetLastError = true on every import whose failure code is actually read.
    //
    // Without it the source-generated marshalling never captures the Win32 error, and
    // Marshal.GetLastPInvokeError() returns 0 for a call that certainly failed. That is how the
    // most useful diagnostic in this project came to be useless: "WintunCreateAdapter returns
    // error 5 - the process is not running as LocalSystem" is the first thing README tells
    // people to check, and the message it prints could only ever say "error 0". Verified by
    // running --install-driver as a normal user: 0 before this line, 5 after.

    internal const int ErrorBufferOverflow = 111;
    internal const int ErrorInvalidData = 13;

    [LibraryImport(Dll, EntryPoint = "WintunCreateAdapter", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint WintunCreateAdapter(string name, string tunnelType, nint requestedGuid);

    [LibraryImport(Dll, EntryPoint = "WintunOpenAdapter", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint WintunOpenAdapter(string name);

    [LibraryImport(Dll, EntryPoint = "WintunCloseAdapter")]
    internal static partial void WintunCloseAdapter(nint adapter);

    /// <summary>Removes the driver from the system entirely - only call this on uninstall.</summary>
    [LibraryImport(Dll, EntryPoint = "WintunDeleteDriver", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WintunDeleteDriver();

    /// <summary>Gets the adapter LUID, needed to look up the interface index for route work.</summary>
    [LibraryImport(Dll, EntryPoint = "WintunGetAdapterLUID")]
    internal static partial void WintunGetAdapterLuid(nint adapter, out ulong luid);

    [LibraryImport(Dll, EntryPoint = "WintunGetRunningDriverVersion")]
    internal static partial uint WintunGetRunningDriverVersion();

    [LibraryImport(Dll, EntryPoint = "WintunStartSession", SetLastError = true)]
    internal static partial nint WintunStartSession(nint adapter, uint capacity);

    [LibraryImport(Dll, EntryPoint = "WintunEndSession")]
    internal static partial void WintunEndSession(nint session);

    /// <summary>Event signalled when a packet arrives - wait on it instead of spinning the CPU.</summary>
    [LibraryImport(Dll, EntryPoint = "WintunGetReadWaitEvent")]
    internal static partial nint WintunGetReadWaitEvent(nint session);

    /// <summary>Returns a pointer into the ring buffer, or IntPtr.Zero with ERROR_NO_MORE_ITEMS when empty.</summary>
    [LibraryImport(Dll, EntryPoint = "WintunReceivePacket", SetLastError = true)]
    internal static partial nint WintunReceivePacket(nint session, out uint packetSize);

    [LibraryImport(Dll, EntryPoint = "WintunReleaseReceivePacket")]
    internal static partial void WintunReleaseReceivePacket(nint session, nint packet);

    [LibraryImport(Dll, EntryPoint = "WintunAllocateSendPacket", SetLastError = true)]
    internal static partial nint WintunAllocateSendPacket(nint session, uint packetSize);

    [LibraryImport(Dll, EntryPoint = "WintunSendPacket")]
    internal static partial void WintunSendPacket(nint session, nint packet);
}

/// <summary>P/Invoke into iphlpapi.dll and kernel32.dll for routing and event waiting.</summary>
internal static partial class WinApi
{
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;
    internal const uint Infinite = 0xFFFFFFFF;

    /// <summary>Converts an adapter LUID into the interface index that route/netsh need.</summary>
    [LibraryImport("iphlpapi.dll", EntryPoint = "ConvertInterfaceLuidToIndex")]
    internal static partial uint ConvertInterfaceLuidToIndex(in ulong luid, out uint index);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForMultipleObjects")]
    internal static partial uint WaitForMultipleObjects(
        uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
}
