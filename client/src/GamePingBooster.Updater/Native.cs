using System.Runtime.InteropServices;

namespace GamePingBooster.Updater;

/// <summary>The Win32, GDI and service-manager calls the window needs. Source-generated stubs: Native AOT.</summary>
internal static unsafe partial class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint CS_DROPSHADOW = 0x00020000;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_DPICHANGED = 0x02E0;
    public const nint HTCAPTION = 2;

    public const int SW_SHOW = 5;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int TRANSPARENT = 1;
    public const uint DT_LEFT = 0x0000;
    public const uint DT_RIGHT = 0x0002;
    public const uint DT_VCENTER = 0x0004;
    public const uint DT_SINGLELINE = 0x0020;
    public const uint DT_NOPREFIX = 0x0800;
    public const uint DT_END_ELLIPSIS = 0x8000;
    public const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    // ---------------------------------------------------------------- user32

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint func);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    public static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint monitor, MONITORINFO* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT pt);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PrivateExtractIconsW(string file, int index, int cx, int cy, nint* icons, uint* ids, uint count, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint hdc, RECT* rect, nint brush);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(nint hdc, string text, int count, RECT* rect, uint format);

    // ---------------------------------------------------------------- gdi32

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint dest, int x, int y, int cx, int cy, nint src, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    public static partial nint AddFontMemResourceEx(void* font, uint size, nint reserved, uint* installed);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextExtentPoint32W(nint hdc, string text, int count, out SIZE size);

    // ---------------------------------------------------------------- dwmapi

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    // ---------------------------------------------------------------- kernel32

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WaitNamedPipeW(string name, uint timeout);

    // ---------------------------------------------------------------- advapi32 (read-only service queries)

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
            dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenServiceW(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(nint service, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    /// <summary>SERVICE_RUNNING (4), STOPPED (1) and so on, or null when the service cannot be read.</summary>
    public static uint? ServiceState(string name)
    {
        // SC_MANAGER_CONNECT, SERVICE_QUERY_STATUS: what the service's default security grants interactive users.
        var manager = OpenSCManagerW(null, null, 0x0001);
        if (manager == 0) return null;
        try
        {
            var service = OpenServiceW(manager, name, 0x0004);
            if (service == 0) return null;
            try
            {
                return QueryServiceStatus(service, out var status) ? status.dwCurrentState : null;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// The service's pipe exists - the new service is listening. WaitNamedPipe neither connects nor takes the
    /// pipe's one instance away from the app: it succeeds when an instance is free, and times out (121) when the
    /// pipe exists but is busy, which is just as good an answer.
    /// </summary>
    public static bool PipeListening(string pipeName)
    {
        if (WaitNamedPipeW($@"\\.\pipe\{pipeName}", 1)) return true;
        return Marshal.GetLastPInvokeError() == 121;
    }

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}

/// <summary>
/// The few GDI+ calls used for anti-aliased shapes - the rounded bar, the step circles, the spinner. GDI draws
/// them with jagged edges. Text stays in GDI: GDI+ cannot see a font added with AddFontMemResourceEx.
/// </summary>
internal static unsafe partial class Gdip
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [LibraryImport("gdiplus.dll")]
    private static partial int GdiplusStartup(out nint token, StartupInput* input, nint output);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipCreateFromHDC(nint hdc, out nint graphics);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipSetSmoothingMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipCreateSolidFill(uint argb, out nint brush);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDeleteBrush(nint brush);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipSetPenStartCap(nint pen, int cap);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipSetPenEndCap(nint pen, int cap);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDeletePen(nint pen);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipCreatePath(int fillMode, out nint path);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDeletePath(nint path);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipAddPathArc(nint path, float x, float y, float width, float height, float start, float sweep);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipClosePathFigure(nint path);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipFillPath(nint graphics, nint brush, nint path);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float width, float height);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDrawEllipse(nint graphics, nint pen, float x, float y, float width, float height);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDrawArc(nint graphics, nint pen, float x, float y, float width, float height, float start, float sweep);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);

    public static void Startup()
    {
        var input = new StartupInput { GdiplusVersion = 1 };
        GdiplusStartup(out _, &input, 0);
    }

    public static uint Argb(uint rgb, byte alpha = 255) => ((uint)alpha << 24) | rgb;
}
