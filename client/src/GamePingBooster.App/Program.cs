using Avalonia;

namespace GamePingBooster.App;

internal static class Program
{
    // No async Main: Avalonia must own the main thread.
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // The IDE designer calls this too, so the name and signature must stay as they are.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
