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
            // Silence is the specified behaviour: a second launch does nothing. Nothing is
            // written to a log either, because there is no log on this side and popping a
            // message box would be its own kind of noise.
            //
            // Exit code 0, not an error: being already open is a normal outcome of double
            // clicking an icon, and a non-zero code would make a shortcut or a script think
            // something went wrong.
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
