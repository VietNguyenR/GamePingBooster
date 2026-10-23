using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;

namespace GamePingBooster.App;

internal static class Program
{
    /// <summary>
    /// Held for the life of the process. A named mutex is only owned while something holds it,
    /// so this must not be collected - and it must not be disposed either, because the point is
    /// that Windows releases it when the process ends, however it ends.
    /// </summary>
    private static Mutex? _instanceLock;

    /// <summary>
    /// Local\, not Global\.
    ///
    /// Local means per logon session, which is the right scope for a window: the thing being
    /// prevented is one person's second double-click, and two people signed in with fast user
    /// switching each get their own UI, their own refresh token and their own %LOCALAPPDATA%.
    /// A Global mutex would also need an ACL to be openable by a second user at all, and the
    /// failure when it is not is indistinguishable from "already running" - which would lock the
    /// second user out of an app that is not running for them.
    ///
    /// The service is machine-wide and stays that way. It is not affected by any of this; the
    /// pipe it serves accepts one client at a time regardless.
    /// </summary>
    private const string InstanceMutexName = @"Local\GamePingBooster.SingleInstance";

    // No async Main: Avalonia must own the main thread.
    [STAThread]
    public static int Main(string[] args)
    {
        if (!TryTakeInstanceLock())
        {
            // A second launch asks the running app to show its window and leaves (owner,
            // 2026-10-23): opening the app from the desktop while it sits in the tray used to do
            // nothing at all, which reads as a broken app. No log and no message box: there is no
            // log on this side, and the window coming up is the answer.
            //
            // Exit code 0, not an error: being already open is a normal outcome of double
            // clicking an icon, and a non-zero code would make a shortcut or a script think
            // something went wrong.
            SignalRunningInstance();
            return 0;
        }

        _showRequest = CreateShowRequest();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Set by a second launch, waited on by the first. Auto-reset: one launch, one show. A launch
    /// that comes before the app has started listening stays set until it does, so it is not lost.
    /// </summary>
    private const string ShowRequestName = @"Local\GamePingBooster.ShowWindow";

    /// <summary>Held for the life of the process, like the mutex: the name only exists while something holds it.</summary>
    private static EventWaitHandle? _showRequest;

    private static EventWaitHandle? CreateShowRequest()
    {
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Calls <paramref name="onShowRequested"/> on a background thread each time another launch
    /// asks for the window. Nothing happens when the handle could not be created: a second launch
    /// then does nothing, as it did before.
    /// </summary>
    public static void ListenForShowRequests(Action onShowRequested)
    {
        if (_showRequest is not { } handle) return;
        // Background: it must not keep the process alive once the app has shut down.
        new Thread(() =>
        {
            while (true)
            {
                handle.WaitOne();
                onShowRequested();
            }
        }) { IsBackground = true, Name = "show-request" }.Start();
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowRequestName, out var handle)) return; // an older build, which does not listen
            using (handle)
            {
                // This process was just started by the user and may take the foreground; the
                // running one, sitting in the tray, may not. Without passing it on, Activate()
                // there only flashes the taskbar button instead of bringing the window to front.
                AllowSetForegroundWindow(AsfwAny);
                handle.Set();
            }
        }
        catch (Exception)
        {
            // Nothing to fall back to: the app is open, just not brought forward.
        }
    }

    private const uint AsfwAny = unchecked((uint)-1);

    // DllImport rather than LibraryImport, as elsewhere in this app: see Loc.cs.
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>
    /// True when this process is the first one. False when another instance already holds it.
    /// </summary>
    private static bool TryTakeInstanceLock()
    {
        try
        {
            _instanceLock = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            if (createdNew) return true;

            _instanceLock.Dispose();
            _instanceLock = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing - a crash, or being killed. The mutex
            // is now ours and the app it belonged to is gone, so this IS the only instance.
            return true;
        }
        catch (Exception)
        {
            // Something refused to create the mutex at all. Starting is the safer failure: an
            // app that will not open because of a locking primitive is worse than two windows.
            return true;
        }
    }

    // The IDE designer calls this too, so the name and signature must stay as they are.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
