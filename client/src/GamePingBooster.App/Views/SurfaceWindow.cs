using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GamePingBooster.App.Views;

/// <summary>
/// The window chrome every window in this app shares: one continuous dark surface that reaches
/// under the system caption, and a strip at the top you can drag.
///
/// It exists because the main window had all of this and the three dialogs had none of it, so
/// opening Settings or Sign in put a grey Windows title bar in the middle of an otherwise dark
/// app. Four copies of the same three properties would have drifted the moment one of them was
/// touched; this is the one place they live.
///
/// What the extended client area does and does not do is worth stating, because the first
/// attempt at it on the main window got it wrong twice. ExtendClientAreaToDecorationsHint makes
/// the client area reach under the caption, so the window is one surface in our own colour.
/// Avalonia 12 still draws the title text and the caption buttons on top of that, and that is
/// wanted: drawing our own as well produced two titles in the same place and seven caption
/// buttons. ExtendClientAreaChromeHints, the Avalonia 11 way of suppressing the system ones,
/// does not exist in Avalonia 12 and neither does Window.HasTitleBar - the build fails with
/// AVLN2000 if you reach for either.
///
/// So a window using this supplies only a spacer at the top: something to keep its content clear
/// of the buttons, and something to drag.
/// </summary>
public class SurfaceWindow : Window
{
    /// <summary>The app's background. #0F172A, the same value the XAML used to repeat.</summary>
    private static readonly IBrush Surface = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));

    protected SurfaceWindow()
    {
        // In the constructor rather than in a style: these are read when the platform window is
        // created, and a style that resolves later would apply them after the frame already
        // exists - which shows up as a window that is dark everywhere except its title bar.
        Background = Surface;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
    }

    /// <summary>
    /// Drag the window by the strip under the caption.
    ///
    /// Avalonia's own title bar handles dragging in the area it occupies; this covers the rest of
    /// the strip, so the whole top of the window behaves the way people expect rather than only
    /// the part with the buttons on it.
    ///
    /// protected, not private: the XAML compiler resolves a handler named here against the
    /// derived window, which can see it.
    /// </summary>
    protected void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
