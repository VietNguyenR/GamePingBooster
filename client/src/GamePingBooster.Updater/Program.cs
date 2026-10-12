using System.Runtime.InteropServices;

namespace GamePingBooster.Updater;

/// <summary>
/// gpb-updater.exe - started by the app (UpdaterLauncher), from a copy in %TEMP%, as the signed-in user.
///
///   --mode check|install   check: the app is opening and asked for a check; install: the service offered one
///   --since &lt;unix ms&gt;       progress written before this belongs to another run
///   --ui-pid &lt;pid&gt;          the app that started this window
///   --app &lt;path&gt;           the installed GamePingBooster.exe, started again once the update is in
///   --lang vi|en
///   --demo                 plays a made-up update, to look at the window; changes nothing
///
/// It installs nothing and asks for nothing: setup is run by the service, as SYSTEM, from a folder only
/// SYSTEM writes. This only reads how it is going and says so.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        var strings = Strings.For(options.Lang);
        try
        {
            LoadFonts();
            Gdip.Startup();
        }
        catch (Exception)
        {
            // Without Inter, GDI falls back to a system font; without GDI+ the shapes are missing. Still a window.
        }
        var flow = new UpdateFlow(options, strings);
        return new SplashWindow(flow, strings).Run();
    }

    /// <summary>The app's font, private to this process (AddFontMemResourceEx): nothing is installed.</summary>
    private static unsafe void LoadFonts()
    {
        var assembly = typeof(Program).Assembly;
        foreach (var name in new[] { "Inter-Regular.ttf", "Inter-SemiBold.ttf", "Inter-Bold.ttf" })
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            // Kept for the life of the process: under 1 MB, and the window lives for a minute.
            var memory = NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(memory, bytes.Length));
            uint installed;
            Native.AddFontMemResourceEx(memory, (uint)bytes.Length, 0, &installed);
        }
    }
}
