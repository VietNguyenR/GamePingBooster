using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static GamePingBooster.Updater.Native;

namespace GamePingBooster.Updater;

/// <summary>
/// The window: one borderless, dark, rounded surface in the app's colours and font, drawn by hand.
///
/// Colours are the app's (SurfaceWindow #111111, cards #242424, MainWindow's #ababab labels, the #2563EB
/// accent of its buttons); the font is the app's Inter. Sizes are in the app's units (DIPs) and scaled to the
/// monitor's DPI. Everything is painted into a back buffer once per frame - text with GDI (the private font),
/// shapes with GDI+ (anti-aliased).
/// </summary>
internal sealed unsafe class SplashWindow
{
    // ---- the app's palette, 0xRRGGBB
    private const uint Surface = 0x111111;
    private const uint Card = 0x1C1C1C;
    private const uint Track = 0x242424;
    private const uint TextPrimary = 0xF8FAFC;
    private const uint TextSecondary = 0xABABAB;
    private const uint TextMuted = 0x787878;
    private const uint TextPending = 0x5E5E5E;
    private const uint Accent = 0x2563EB;
    private const uint AccentLight = 0x60A5FA;
    private const uint Ring = 0x3A3A3A;
    private const uint Danger = 0xB91C1C;
    private const uint DangerText = 0xFCA5A5;

    // ---- layout, in DIPs
    private const int Width = 440;
    private const int Pad = 24;
    private const int BarTop = 122;
    private const int CompactHeight = BarTop + 6 + 28;
    private const int CardTop = 150;
    private const int RowHeight = 30;
    private const int CardPad = 10;
    private static readonly int CardBottom = CardTop + CardPad * 2 + RowHeight * View.StepCount;
    private static readonly int FullHeight = CardBottom + 44;

    private const string ClassName = "GamePingBoosterUpdater";
    private const nuint AnimationTimer = 1;
    private const nuint FlowTimer = 2;

    private static SplashWindow? _instance;

    private readonly UpdateFlow _flow;
    private readonly Strings _s;
    private nint _hwnd;
    private float _scale = 1;
    private bool _compactShown;
    private nint _fontTitle, _fontName, _fontBody, _fontSmall, _fontStep, _fontStepActive;
    private nint _icon;
    private readonly long _startTicks = Environment.TickCount64;

    public SplashWindow(UpdateFlow flow, Strings strings)
    {
        _flow = flow;
        _s = strings;
    }

    public int Run()
    {
        _instance = this;
        var instance = GetModuleHandleW(null);

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_DROPSHADOW,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(0, 32512), // IDC_ARROW
                lpszClassName = className,
            };
            RegisterClassExW(&wc);
        }

        _compactShown = _flow.View.Compact;
        _hwnd = CreateWindowExW(WS_EX_APPWINDOW, ClassName, _s.AppName, WS_POPUP,
            0, 0, Width, CompactHeight, 0, 0, instance, 0);
        if (_hwnd == 0) return 1;

        // Windows 11: rounded corners and a hairline border in the app's card colour. Ignored on Windows 10.
        int corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33, &corner, sizeof(int));
        uint border = Gdi(0x2A2A2A);
        DwmSetWindowAttribute(_hwnd, 34, &border, sizeof(uint));
        int dark = 1;
        DwmSetWindowAttribute(_hwnd, 20, &dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE

        ApplyDpi(GetDpiForWindow(_hwnd));
        PlaceCentred();
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);

        SetTimer(_hwnd, AnimationTimer, 33, 0);
        SetTimer(_hwnd, FlowTimer, 200, 0);
        _flow.Tick();

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        return 0;
    }

    private int CurrentHeight => _compactShown ? CompactHeight : FullHeight;

    private int S(float dips) => (int)MathF.Round(dips * _scale);

    private void ApplyDpi(uint dpi)
    {
        _scale = dpi == 0 ? 1 : dpi / 96f;
        foreach (var f in new[] { _fontTitle, _fontName, _fontBody, _fontSmall, _fontStep, _fontStepActive })
        {
            if (f != 0) DeleteObject(f);
        }
        _fontTitle = Font("Inter SemiBold", 18, 600);
        _fontName = Font("Inter SemiBold", 13, 600);
        _fontBody = Font("Inter", 13, 400);
        _fontSmall = Font("Inter", 12, 400);
        _fontStep = Font("Inter", 13, 400);
        _fontStepActive = Font("Inter SemiBold", 13, 600);

        if (_icon != 0) DestroyIcon(_icon);
        nint icon;
        uint id;
        var size = S(22);
        _icon = PrivateExtractIconsW(Environment.ProcessPath ?? "", 0, size, size, &icon, &id, 1, 0) == 1 ? icon : 0;
    }

    private nint Font(string face, float sizeDips, int weight) =>
        // CLEARTYPE_QUALITY (5): the same smoothing the rest of Windows uses on this screen.
        CreateFontW(-S(sizeDips), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);

    /// <summary>Centred on the monitor the mouse is on - where the player is looking.</summary>
    private void PlaceCentred()
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(monitor, &info);
        var work = info.rcWork;
        int w = S(Width), h = S(CurrentHeight);
        var x = work.Left + (work.Right - work.Left - w) / 2;
        var y = work.Top + (work.Bottom - work.Top - S(FullHeight)) / 2;
        SetWindowPos(_hwnd, 0, x, y, w, h, SWP_NOZORDER);
    }

    /// <summary>Compact to full when there is an update to show: grows downwards from where it is.</summary>
    private void Resize()
    {
        GetWindowRect(_hwnd, out var r);
        SetWindowPos(_hwnd, 0, r.Left, r.Top, S(Width), S(CurrentHeight), SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = _instance;
        if (self is null) return DefWindowProcW(hwnd, msg, wParam, lParam);
        try
        {
            switch (msg)
            {
                case WM_PAINT:
                    self.Paint(hwnd);
                    return 0;
                case WM_ERASEBKGND:
                    return 1;
                case WM_NCHITTEST:
                    // The whole window drags, like the app's title strip.
                    return HTCAPTION;
                case WM_TIMER:
                    self.OnTimer(wParam);
                    return 0;
                case WM_DPICHANGED:
                    self.ApplyDpi((uint)(wParam & 0xFFFF));
                    var suggested = (RECT*)lParam;
                    SetWindowPos(hwnd, 0, suggested->Left, suggested->Top, self.S(Width), self.S(self.CurrentHeight), SWP_NOZORDER | SWP_NOACTIVATE);
                    return 0;
                case WM_CLOSE:
                    // Alt+F4 while setup runs closes only the window: the update carries on, and the service starts
                    // the app if nothing else does (AutoUpdater.RelaunchUnlessAppComesAsync).
                    DestroyWindow(hwnd);
                    return 0;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception)
        {
            // A drawing error must not take the message loop down; the next frame tries again.
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnTimer(nint id)
    {
        if ((nuint)id == FlowTimer)
        {
            _flow.Tick();
            if (_flow.Finished)
            {
                DestroyWindow(_hwnd);
                return;
            }
            if (_flow.View.Compact != _compactShown)
            {
                _compactShown = _flow.View.Compact;
                Resize();
            }
        }
        InvalidateRect(_hwnd, 0, false);
    }

    // ------------------------------------------------------------------ painting

    private void Paint(nint hwnd)
    {
        PAINTSTRUCT ps;
        var hdc = BeginPaint(hwnd, &ps);
        GetClientRect(hwnd, out var client);
        int w = client.Right, h = client.Bottom;

        var mem = CreateCompatibleDC(hdc);
        var bitmap = CreateCompatibleBitmap(hdc, w, h);
        var oldBitmap = SelectObject(mem, bitmap);
        try
        {
            var bg = CreateSolidBrush(Gdi(Surface));
            FillRect(mem, &client, bg);
            DeleteObject(bg);

            Gdip.GdipCreateFromHDC(mem, out var g);
            Gdip.GdipSetSmoothingMode(g, 4); // AntiAlias
            Gdip.GdipSetPixelOffsetMode(g, 4); // Half
            DrawShapes(g);
            Gdip.GdipDeleteGraphics(g);

            DrawTexts(mem);
            BitBlt(hdc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
        }
        finally
        {
            SelectObject(mem, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(mem);
            EndPaint(hwnd, &ps);
        }
    }

    private float Seconds => (Environment.TickCount64 - _startTicks) / 1000f;

    private void DrawShapes(nint g)
    {
        var v = _flow.View;

        // ---- the bar
        float left = S(Pad), right = S(Width - Pad), top = S(BarTop), height = S(6);
        RoundRect(g, Track, left, top, right - left, height, height / 2);
        var fill = v.Error ? Danger : Accent;
        if (v.Progress is { } p)
        {
            var width = (right - left) * (float)Math.Clamp(p, 0, 1);
            if (width > 0.5f) RoundRect(g, fill, left, top, Math.Max(width, height), height, height / 2);
        }
        else
        {
            // Moving segment: 30% of the track, sweeping across every 1.5 s.
            var span = right - left;
            var phase = Seconds % 1.5f / 1.5f;
            var segment = span * 0.3f;
            var x0 = left - segment + (span + segment) * Ease(phase);
            var a = Math.Max(left, x0);
            var b = Math.Min(right, x0 + segment);
            if (b - a > 1) RoundRect(g, AccentLight, a, top, b - a, height, height / 2);
        }

        if (_compactShown) return;

        // ---- the card of steps
        RoundRect(g, Card, S(Pad), S(CardTop), S(Width - Pad * 2), S(CardBottom - CardTop), S(12));
        for (var i = 0; i < View.StepCount; i++)
        {
            var cx = S(Pad + 16 + 9);
            var cy = S(CardTop + CardPad + RowHeight * i + RowHeight / 2f);
            DrawIndicator(g, v.Steps[i], cx, cy, S(9));
        }
    }

    private static float Ease(float t) => t < 0.5f ? 2 * t * t : 1 - MathF.Pow(-2 * t + 2, 2) / 2;

    private void DrawIndicator(nint g, StepState state, float cx, float cy, float r)
    {
        switch (state)
        {
            case StepState.Done:
            {
                Circle(g, Accent, cx, cy, r);
                Gdip.GdipCreatePen1(Gdip.Argb(0xFFFFFF), S(1.8f), 2, out var pen);
                Gdip.GdipSetPenStartCap(pen, 2);
                Gdip.GdipSetPenEndCap(pen, 2);
                Gdip.GdipDrawLine(g, pen, cx - r * 0.42f, cy + r * 0.02f, cx - r * 0.12f, cy + r * 0.32f);
                Gdip.GdipDrawLine(g, pen, cx - r * 0.12f, cy + r * 0.32f, cx + r * 0.45f, cy - r * 0.3f);
                Gdip.GdipDeletePen(pen);
                break;
            }
            case StepState.Failed:
            {
                Circle(g, Danger, cx, cy, r);
                Gdip.GdipCreatePen1(Gdip.Argb(0xFFFFFF), S(1.8f), 2, out var pen);
                Gdip.GdipSetPenStartCap(pen, 2);
                Gdip.GdipSetPenEndCap(pen, 2);
                var d = r * 0.36f;
                Gdip.GdipDrawLine(g, pen, cx - d, cy - d, cx + d, cy + d);
                Gdip.GdipDrawLine(g, pen, cx + d, cy - d, cx - d, cy + d);
                Gdip.GdipDeletePen(pen);
                break;
            }
            case StepState.Active:
            {
                var rr = r - S(1);
                Gdip.GdipCreatePen1(Gdip.Argb(Ring), S(2), 2, out var track);
                Gdip.GdipDrawEllipse(g, track, cx - rr, cy - rr, rr * 2, rr * 2);
                Gdip.GdipDeletePen(track);
                Gdip.GdipCreatePen1(Gdip.Argb(AccentLight), S(2), 2, out var arc);
                Gdip.GdipSetPenStartCap(arc, 2);
                Gdip.GdipSetPenEndCap(arc, 2);
                Gdip.GdipDrawArc(g, arc, cx - rr, cy - rr, rr * 2, rr * 2, Seconds * 360 % 360, 100);
                Gdip.GdipDeletePen(arc);
                break;
            }
            default:
            {
                var rr = r - S(1);
                Gdip.GdipCreatePen1(Gdip.Argb(Ring), S(1.5f), 2, out var pen);
                Gdip.GdipDrawEllipse(g, pen, cx - rr, cy - rr, rr * 2, rr * 2);
                Gdip.GdipDeletePen(pen);
                break;
            }
        }
    }

    private static void Circle(nint g, uint rgb, float cx, float cy, float r)
    {
        Gdip.GdipCreateSolidFill(Gdip.Argb(rgb), out var brush);
        Gdip.GdipFillEllipse(g, brush, cx - r, cy - r, r * 2, r * 2);
        Gdip.GdipDeleteBrush(brush);
    }

    private static void RoundRect(nint g, uint rgb, float x, float y, float w, float h, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(w, h));
        Gdip.GdipCreatePath(0, out var path);
        Gdip.GdipAddPathArc(path, x, y, d, d, 180, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        Gdip.GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        Gdip.GdipClosePathFigure(path);
        Gdip.GdipCreateSolidFill(Gdip.Argb(rgb), out var brush);
        Gdip.GdipFillPath(g, brush, path);
        Gdip.GdipDeleteBrush(brush);
        Gdip.GdipDeletePath(path);
    }

    private void DrawTexts(nint hdc)
    {
        var v = _flow.View;
        SetBkMode(hdc, TRANSPARENT);

        // ---- header: icon, name, versions
        if (_icon != 0) DrawIconEx(hdc, S(Pad), S(21), _icon, S(22), S(22), 0, 0, 3);
        Text(hdc, _fontName, TextPrimary, _s.AppName, Pad + 32, 20, Width - Pad, 44, DT_LEFT);
        if (v.Versions is { } versions) Text(hdc, _fontSmall, TextMuted, versions, Pad, 20, Width - Pad, 44, DT_RIGHT);

        // ---- title and detail
        Text(hdc, _fontTitle, v.Error ? DangerText : TextPrimary, v.Title, Pad, 58, Width - Pad, 88, DT_LEFT);
        Text(hdc, _fontBody, TextSecondary, v.Detail, Pad, 88, Width - Pad, 110, DT_LEFT);

        if (_compactShown) return;

        // ---- steps
        var labels = _s.Steps;
        for (var i = 0; i < View.StepCount; i++)
        {
            var top = CardTop + CardPad + RowHeight * i;
            var state = v.Steps[i];
            var (font, color) = state switch
            {
                StepState.Active => (_fontStepActive, TextPrimary),
                StepState.Done => (_fontStep, TextSecondary),
                StepState.Failed => (_fontStepActive, DangerText),
                _ => (_fontStep, TextPending),
            };
            var labelRight = Width - Pad - 16 - (state == StepState.Active && v.Note is not null ? 48 : 0);
            Text(hdc, font, color, labels[i], Pad + 16 + 9 + 9 + 12, top, labelRight, top + RowHeight, DT_LEFT);
            if (state == StepState.Active && v.Note is { } note)
            {
                Text(hdc, _fontSmall, TextSecondary, note, Width - Pad - 16 - 60, top, Width - Pad - 16, top + RowHeight, DT_RIGHT);
            }
        }

        // ---- footer
        if (v.Footer is { } footer)
        {
            Text(hdc, _fontSmall, TextMuted, footer, Pad, CardBottom + 12, Width - Pad, CardBottom + 32, DT_LEFT);
        }
    }

    private void Text(nint hdc, nint font, uint rgb, string text, int left, int top, int right, int bottom, uint align)
    {
        var old = SelectObject(hdc, font);
        SetTextColor(hdc, Gdi(rgb));
        var rect = new RECT { Left = S(left), Top = S(top), Right = S(right), Bottom = S(bottom) };
        DrawTextW(hdc, text, text.Length, &rect, align | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
        SelectObject(hdc, old);
    }

    /// <summary>0xRRGGBB to GDI's COLORREF (0x00BBGGRR).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Gdi(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
}
