using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace GamePingBooster.App.Services;

/// <summary>
/// "Still running in the tray": the notification Windows shows for a tray icon's balloon - a toast on
/// Windows 10 and 11, with the app's name and icon - the first time the window goes to the tray in a run.
///
/// Avalonia's TrayIcon has no way to show one (Avalonia 12.1: icon, tooltip, menu, click), so this asks
/// Windows directly: Shell_NotifyIcon(NIM_MODIFY, NIF_INFO) on Avalonia's own icon. That icon is known to
/// Windows by a window handle and an id, and both are Avalonia's internals - Win32Platform.Instance.Handle
/// and TrayIconImpl._uniqueId, as its UpdateIcon passes them (read from Avalonia.Win32 12.1.1). They are
/// read by reflection, kept for Native AOT by the DynamicDependency attributes below, and anything that does
/// not match - a later Avalonia that renamed them, another platform - means no notification and nothing else.
/// A second icon of our own would have been a second icon in the tray.
/// </summary>
public static class TrayNotice
{
    private const int NimModify = 0x1;
    private const int NifInfo = 0x10;
    private const int NiifInfo = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // DllImport rather than LibraryImport, as elsewhere in this app: see Loc.cs.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    /// <summary>Shows <paramref name="title"/> and <paramref name="text"/> from <paramref name="icon"/>. False when it could not.</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(TrayIcon))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "Avalonia.Win32.TrayIconImpl", "Avalonia.Win32")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties,
        "Avalonia.Win32.Win32Platform", "Avalonia.Win32")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The members read are kept by the DynamicDependency attributes above.")]
    [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "A constant type name, kept by the DynamicDependency attributes above.")]
    public static bool Show(TrayIcon icon, string title, string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var impl = typeof(TrayIcon).GetProperty("Impl", any)?.GetValue(icon);
            if (impl?.GetType().GetField("_uniqueId", any)?.GetValue(impl) is not int id) return false;

            var platformType = Type.GetType("Avalonia.Win32.Win32Platform, Avalonia.Win32");
            var platform = platformType?.GetProperty("Instance", any)?.GetValue(null);
            if (platform?.GetType().GetProperty("Handle", any)?.GetValue(platform) is not IntPtr hwnd || hwnd == IntPtr.Zero) return false;

            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = hwnd,
                uID = id,
                uFlags = NifInfo,
                szTip = "",
                szInfo = text.Length > 255 ? text[..255] : text,
                szInfoTitle = title.Length > 63 ? title[..63] : title,
                dwInfoFlags = NiifInfo,
            };
            return Shell_NotifyIcon(NimModify, ref data);
        }
        catch (Exception)
        {
            return false; // A nicety: see the class summary.
        }
    }
}
