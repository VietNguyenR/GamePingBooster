using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GamePingBooster.OverlayCheck;

/// <summary>
/// Prototype for "show warnings over the game": does an ordinary window of ours show on top of a
/// game running fullscreen, with no DLL in the game and nothing done to the game process?
///
///     dotnet run --project client/src/GamePingBooster.OverlayCheck            (plain topmost window)
///     install-uiaccess-test.ps1, as admin                                      (UIAccess build, signed, in Program Files)
///
/// Every --every seconds (default 20) it shows a sample warning card in the top-left corner of the
/// monitor the foreground window is on, for --show seconds (default 5), then hides it. Ctrl+Alt+Shift+P
/// shows it now, Ctrl+Alt+Shift+O quits; it also quits by itself after --minutes (default 120).
///
/// The card cannot tell whether it was actually seen - in exclusive fullscreen the game owns the
/// screen and our window simply never reaches it. So the card prints what the log prints: how
/// Windows classifies the foreground app (SHQueryUserNotificationState: D3D exclusive fullscreen vs
/// busy/borderless), which process it is, and whether this process got UIAccess. The player reports
/// "seen / not seen" per game and display mode; the log tells which mode that was.
///
/// Window: WS_POPUP with WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED |
/// WS_EX_TOOLWINDOW - never takes focus from the game (a focus change is what drops a game out of
/// exclusive fullscreen), clicks go through it, no taskbar button. Plain GDI, no Avalonia: the real
/// overlay will be a small separate exe for the same UIAccess reason, so this is the shape it takes.
/// </summary>
internal static unsafe class Program
{
    private static int _everySec = 20;
    private static int _showSec = 5;
    private static int _minutes = 120;

    private static IntPtr _hwnd;
    private static readonly WndProcDelegate Proc = WindowProc; // kept alive: Windows holds a raw pointer to it
    private static bool _uiAccess;
    private static int _tick;
    private static int _nextShowAt = 3;
    private static int _hideAt = -1;
    private static int _zCheckAt = -1;
    private static bool _visible;
    private static string _lastKey = "";

    private static string _title = "";
    private static string _line1 = "";
    private static string _line2 = "";

    private static uint _fontDpi;
    private static IntPtr _titleFont, _bodyFont;
    private static double _scale = 1;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamePingBooster", "overlay-check.log");

    [STAThread]
    private static int Main(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (!int.TryParse(args[i + 1], out var v) || v <= 0) continue;
            switch (args[i])
            {
                case "--every": _everySec = v; break;
                case "--show": _showSec = v; break;
                case "--minutes": _minutes = v; break;
            }
        }

        using var single = new Mutex(true, @"Local\GpbOverlayCheck", out var first);
        if (!first) return 1;

        _uiAccess = HasUiAccess();
        Log($"start  uiAccess={_uiAccess}  exe={Environment.ProcessPath}  os={Environment.OSVersion.Version}  every={_everySec}s show={_showSec}s minutes={_minutes}");

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = hInstance,
            lpszClassName = "GpbOverlayCheck",
        };
        if (RegisterClassExW(ref wc) == 0) { Log($"RegisterClassEx failed {Marshal.GetLastWin32Error()}"); return 2; }

        _hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW,
            "GpbOverlayCheck", "Game Ping Booster overlay check", WS_POPUP,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { Log($"CreateWindowEx failed {Marshal.GetLastWin32Error()}"); return 3; }

        SetLayeredWindowAttributes(_hwnd, 0, 240, LWA_ALPHA);
        if (!RegisterHotKey(_hwnd, HotkeyQuit, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 'O')) Log("hotkey Ctrl+Alt+Shift+O taken");
        if (!RegisterHotKey(_hwnd, HotkeyShow, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 'P')) Log("hotkey Ctrl+Alt+Shift+P taken");
        SetTimer(_hwnd, UIntPtr.Zero, 1000, IntPtr.Zero);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Log("exit");
        return 0;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                OnTick();
                return IntPtr.Zero;
            case WM_HOTKEY:
                if ((int)wParam == HotkeyQuit) DestroyWindow(hwnd);
                else if ((int)wParam == HotkeyShow) { Log("hotkey: show now"); Show(Probe()); }
                return IntPtr.Zero;
            case WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_NCHITTEST:
                return HTTRANSPARENT;
            case WM_DPICHANGED:
                return IntPtr.Zero; // Show() sizes the card for the monitor it lands on
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void OnTick()
    {
        _tick++;
        var s = Probe();

        // Log only changes: a match is long and the interesting lines are the transitions
        // (alt-tab, loading screen -> match, display mode switched in the game's settings).
        var key = $"{s.Quns}|{s.Process}|{s.CoversMonitor}|{s.HasCaption}|{s.Topmost}";
        if (key != _lastKey)
        {
            _lastKey = key;
            Log($"fg     {s.Process,-24} state={QunsName(s.Quns)}  coversMonitor={s.CoversMonitor}  caption={s.HasCaption}  fgTopmost={s.Topmost}");
        }

        if (_tick >= _minutes * 60) { DestroyWindow(_hwnd); return; }

        if (_tick >= _nextShowAt)
        {
            Show(s);
            return;
        }

        if (!_visible) return;

        if (_tick == _zCheckAt)
            Log($"zorder {ZOrderVersus(s.Window)}  (state={QunsName(s.Quns)}, fg={s.Process})");

        if (_tick >= _hideAt)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _visible = false;
            Log("hide");
            return;
        }

        // Keep it live and keep it on top: a game that makes itself topmost after we showed
        // would otherwise cover the card for the rest of its few seconds.
        SetText(s);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    private static void Show(Snapshot s)
    {
        var monitor = MonitorFromWindow(s.Window != IntPtr.Zero ? s.Window : _hwnd, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(monitor, ref mi);
        if (GetDpiForMonitor(monitor, 0, out var dpi, out _) != 0 || dpi == 0) dpi = 96;
        EnsureFonts(dpi);

        int w = S(400), h = S(92);
        // Top-left: in PUBG the kill feed is top-right, the compass top-centre, the minimap
        // bottom-right; the top-left corner is where Steam puts its FPS counter too.
        int x = mi.rcMonitor.left + S(24), y = mi.rcMonitor.top + S(24);
        SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, S(12), S(12)), false);
        SetText(s);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(_hwnd, IntPtr.Zero, true);

        _visible = true;
        _hideAt = _tick + _showSec;
        _zCheckAt = _tick + 1; // after the compositor has had a frame to place it
        _nextShowAt = _tick + _everySec;
        Log($"show   on {s.Process} state={QunsName(s.Quns)} at {x},{y} {w}x{h} dpi={dpi}  zorder now: {ZOrderVersus(s.Window)}");
    }

    private static void SetText(Snapshot s)
    {
        _title = "Game Ping Booster · thử overlay " + DateTime.Now.ToString("HH:mm:ss");
        _line1 = "Wi-Fi dao động tới router: 18 ms (dòng mẫu)";
        _line2 = $"{QunsLabel(s.Quns)} · {s.Process} · {(_uiAccess ? "UIAccess" : "cửa sổ thường")}";
    }

    private static void Paint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var rc);

        var bg = CreateSolidBrush(Rgb(0x24, 0x24, 0x24));
        FillRect(hdc, ref rc, bg);
        DeleteObject(bg);

        var bar = new RECT { left = 0, top = 0, right = S(5), bottom = rc.bottom };
        var amber = CreateSolidBrush(Rgb(0xfb, 0xbf, 0x24));
        FillRect(hdc, ref bar, amber);
        DeleteObject(amber);

        SetBkMode(hdc, TRANSPARENT);
        var old = SelectObject(hdc, _titleFont);
        SetTextColor(hdc, Rgb(0xe5, 0xe5, 0xe5));
        var r = new RECT { left = S(18), top = S(10), right = rc.right - S(12), bottom = S(34) };
        DrawTextW(hdc, _title, -1, ref r, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);

        SelectObject(hdc, _bodyFont);
        SetTextColor(hdc, Rgb(0xfb, 0xbf, 0x24));
        r = new RECT { left = S(18), top = S(36), right = rc.right - S(12), bottom = S(58) };
        DrawTextW(hdc, _line1, -1, ref r, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);

        SetTextColor(hdc, Rgb(0xab, 0xab, 0xab));
        r = new RECT { left = S(18), top = S(60), right = rc.right - S(12), bottom = S(82) };
        DrawTextW(hdc, _line2, -1, ref r, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);

        SelectObject(hdc, old);
        EndPaint(hwnd, ref ps);
    }

    private static void EnsureFonts(uint dpi)
    {
        if (dpi == _fontDpi) return;
        if (_titleFont != IntPtr.Zero) DeleteObject(_titleFont);
        if (_bodyFont != IntPtr.Zero) DeleteObject(_bodyFont);
        _fontDpi = dpi;
        _scale = dpi / 96.0;
        _titleFont = CreateFontW(-S(15), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        _bodyFont = CreateFontW(-S(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
    }

    private static int S(int v) => (int)Math.Round(v * _scale);

    private readonly record struct Snapshot(IntPtr Window, string Process, int Quns, bool CoversMonitor, bool HasCaption, bool Topmost);

    private static Snapshot Probe()
    {
        if (SHQueryUserNotificationState(out var quns) != 0) quns = 0;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return new Snapshot(IntPtr.Zero, "(none)", quns, false, false, false);

        GetWindowThreadProcessId(fg, out var pid);
        string name;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; }
        catch { name = $"pid {pid}"; }

        var covers = false;
        if (GetWindowRect(fg, out var wr))
        {
            var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (GetMonitorInfoW(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref mi))
                covers = wr.left <= mi.rcMonitor.left && wr.top <= mi.rcMonitor.top
                      && wr.right >= mi.rcMonitor.right && wr.bottom >= mi.rcMonitor.bottom;
        }
        var style = (long)GetWindowLongPtrW(fg, GWL_STYLE);
        var exStyle = (long)GetWindowLongPtrW(fg, GWL_EXSTYLE);
        return new Snapshot(fg, name, quns, covers, (style & WS_CAPTION) == WS_CAPTION, (exStyle & WS_EX_TOPMOST) != 0);
    }

    /// <summary>
    /// Which of the two is higher in the z-order. Only a hint: windows in the UIAccess band are
    /// above every ordinary topmost window whatever this walk says, and in exclusive fullscreen the
    /// z-order does not decide what reaches the screen at all.
    /// </summary>
    private static string ZOrderVersus(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return "no foreground window";
        var root = GetAncestor(fg, GA_ROOT);
        for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (h == _hwnd) return "overlay ABOVE game";
            if (h == root) return "game ABOVE overlay";
        }
        return "not found";
    }

    private static string QunsName(int q) => q switch
    {
        1 => "NOT_PRESENT",
        2 => "BUSY",
        3 => "RUNNING_D3D_FULL_SCREEN",
        4 => "PRESENTATION_MODE",
        5 => "ACCEPTS_NOTIFICATIONS",
        6 => "QUIET_TIME",
        7 => "APP",
        _ => $"?{q}",
    };

    private static string QunsLabel(int q) => q switch
    {
        3 => "Fullscreen độc quyền (D3D)",
        2 => "Toàn màn hình (borderless/FSO)",
        5 => "Cửa sổ bình thường",
        4 => "Chế độ trình chiếu",
        6 => "Quiet time",
        7 => "App toàn màn hình (Store)",
        _ => QunsName(q),
    };

    private static bool HasUiAccess()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return false;
        try
        {
            uint value = 0;
            return GetTokenInformation(token, TokenUIAccess, &value, sizeof(uint), out _) && value != 0;
        }
        finally { CloseHandle(token); }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch
        {
            // A prototype that cannot log still shows its card.
        }
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---- Win32 ---------------------------------------------------------------------------------

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WS_POPUP = 0x80000000;
    private const long WS_CAPTION = 0x00C00000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080,
        WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_MOUSEACTIVATE = 0x0021, WM_NCHITTEST = 0x0084,
        WM_TIMER = 0x0113, WM_HOTKEY = 0x0312, WM_DPICHANGED = 0x02E0;
    private static readonly IntPtr MA_NOACTIVATE = 3, HTTRANSPARENT = -1, HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const int SW_HIDE = 0;
    private const uint LWA_ALPHA = 0x2;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const int HotkeyQuit = 1, HotkeyShow = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    private const uint GA_ROOT = 2, GW_HWNDNEXT = 2;
    private const int TRANSPARENT = 1;
    private const uint CLEARTYPE_QUALITY = 5;
    private const uint DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
    private const uint TOKEN_QUERY = 0x8;
    private const int TokenUIAccess = 26;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int len, ref RECT rc, uint format);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, void* info, uint len, out uint ret);
}
