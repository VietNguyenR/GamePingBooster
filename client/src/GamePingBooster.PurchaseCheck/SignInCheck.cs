using Avalonia;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;

namespace GamePingBooster.PurchaseCheck;

/// <summary>
/// Checks the app's half of "last sign-in wins" - SignInWatcher and the LicenceClient calls under it - against a fake
/// licence server in this process, so it needs nothing running:
///
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- signin
///
/// The server half (and the real /auth/watch) is web-service's `npm run check:sign-in-wins`. Never touches this PC's
/// own sign-in: the watcher is handed its credential, RefreshTokenStore is not read or written.
/// </summary>
internal static class SignInCheck
{
    private static int _failures;
    private static int _passes;

    public static int Run()
    {
        try
        {
            RunAll().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"\nthe check itself failed: {ex}");
        }
        Console.WriteLine(_failures == 0 ? $"\nall {_passes} checks passed" : $"\n{_failures} check(s) FAILED, {_passes} passed");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunAll()
    {
        await ClientParsesEveryAnswer();
        await WatcherFiresOnceWhenEvicted();
        await HeldPollIsNotAHotLoop();
        await WakeEndsTheHeldPoll();
        await StaleAnswerForAnOldSignInIsIgnored();
        await OutageBacksOffWithJitter();
        await OldServerAndSignedOutBackOff();
        await NothingToWatchSendsNothing();
        ReportOnlyActsOnTheCode();
        Message();
    }

    // ------------------------------------------------------------------ cases

    private static async Task ClientParsesEveryAnswer()
    {
        Section("LicenceClient: /auth/watch, /auth/token and /account answers");
        await using var server = new FakeServer();
        using var client = new LicenceClient(server.Url);

        server.Watch = _ => (200, """{"state":"active","wait":0}""");
        var active = await client.WatchSignInAsync("rt", default);
        Check("200 active", active.State, "active");
        Check("bearer sent", server.LastAuth, "Bearer rt");
        Check("version header sent", server.LastVersion is { Length: > 0 }, true);

        server.Watch = _ => (401, Evicted("MAY-NET", 1_791_000_000));
        var evicted = await client.WatchSignInAsync("rt", default);
        Check("401 signed_in_elsewhere", evicted.State, "signed_in_elsewhere");
        Check("names the other machine", evicted.DeviceLabel, "MAY-NET");
        Check("and when", evicted.At, 1_791_000_000L);

        server.Watch = _ => (401, """{"state":"signed_out","error":"x"}""");
        Check("401 signed_out", (await client.WatchSignInAsync("rt", default)).State, "signed_out");

        server.Watch = _ => (404, "<html>not here</html>");
        var notFound = await Catch(() => client.WatchSignInAsync("rt", default));
        Check("404 throws a LicenceException with the status", (notFound as LicenceException)?.StatusCode, HttpStatusCode.NotFound);

        server.Watch = _ => (502, "<html>bad gateway</html>");
        Check("502 throws", (await Catch(() => client.WatchSignInAsync("rt", default)))?.GetType().Name, nameof(LicenceException));

        server.Watch = _ => (401, """{"error":"proxy says no"}""");
        var noState = await Catch(() => client.WatchSignInAsync("rt", default)) as LicenceException;
        Check("401 without a state is an error, not an eviction", noState?.SignedInElsewhere, false);

        server.Token = (401, Evicted("MAY-NET", 1_791_000_000));
        var token = await Catch(() => client.FetchTokenAsync("rt", "04ab", "ME", default)) as LicenceException;
        Check("/auth/token 401: SignedInElsewhere", token?.SignedInElsewhere, true);
        Check("/auth/token 401: other machine", token?.OtherDevice, "MAY-NET");
        Check("/auth/token 401: when", token?.SignedOutAt?.ToUnixTimeSeconds(), 1_791_000_000L);

        server.Account = (401, Evicted(null, null));
        var account = await Catch(() => client.FetchAccountAsync("rt", default)) as LicenceException;
        Check("/account 401 keeps the code now", account?.SignedInElsewhere, true);
        Check("/account 401 with no label", account?.OtherDevice, null);

        server.Account = (402, """{"error":"no plan","code":"expired"}""");
        var expired = await Catch(() => client.FetchAccountAsync("rt", default)) as LicenceException;
        Check("other codes still come through /account", expired?.Code, "expired");
        Check("and are not an eviction", expired?.SignedInElsewhere, false);
    }

    private static async Task WatcherFiresOnceWhenEvicted()
    {
        Section("watcher: an eviction mid-poll is reported at once, and once");
        await using var server = new FakeServer();
        var evict = new TaskCompletionSource();
        server.WatchAsync = async ct =>
        {
            // Held, like the real one, until the other machine signs in.
            await Task.WhenAny(evict.Task, Task.Delay(TimeSpan.FromSeconds(45), ct));
            return evict.Task.IsCompleted ? (401, Evicted("MAY-NET", 1_791_000_000)) : (200, """{"state":"active","wait":0}""");
        };

        var fired = new ConcurrentQueue<SignedOutElsewhere>();
        string? token = "rt-a";
        await using var watcher = new SignInWatcher(fired.Enqueue, () => token);
        watcher.OnStatus(new() { LicenceUrl = server.Url });
        watcher.Start();

        await Until(() => server.WatchCalls >= 1, TimeSpan.FromSeconds(5));
        Check("one poll in flight", server.WatchCalls, 1);
        await Task.Delay(300);
        Check("still only one while it is held", server.WatchCalls, 1);

        var sw = Stopwatch.StartNew();
        evict.SetResult();
        await Until(() => !fired.IsEmpty, TimeSpan.FromSeconds(5));
        Check("reported within a second", sw.ElapsedMilliseconds < 1000, true);
        Check("with the other machine", fired.FirstOrDefault()?.OtherDevice, "MAY-NET");
        Check("and the moment", fired.FirstOrDefault()?.At?.ToUnixTimeSeconds(), 1_791_000_000L);

        // The app has not cleared the credential yet (it does that on the UI thread): no second report, no storm.
        await Task.Delay(1500);
        Check("reported once", fired.Count, 1);
        Check("and stops asking with that sign-in", server.WatchCalls, 1);

        // A renewal saying the same afterwards is the same eviction.
        watcher.Report(new LicenceExceptionProbe().Evicted());
        Check("a renewal's 401 for the same sign-in is not reported again", fired.Count, 1);
    }

    private static async Task HeldPollIsNotAHotLoop()
    {
        Section("watcher: a 200 that is not held (a proxy) is not a hot loop");
        await using var server = new FakeServer { Watch = _ => (200, """{"state":"active","wait":0}""") };
        await using var watcher = new SignInWatcher(_ => { }, () => "rt");
        watcher.OnStatus(new() { LicenceUrl = server.Url });
        watcher.Start();
        await Task.Delay(TimeSpan.FromSeconds(4));
        Check($"at most 1 request in 4 s (got {server.WatchCalls})", server.WatchCalls <= 1, true);
    }

    private static async Task WakeEndsTheHeldPoll()
    {
        Section("watcher: a new sign-in is watched at once, not after the old poll");
        await using var server = new FakeServer();
        server.WatchAsync = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            return (200, """{"state":"active","wait":0}""");
        };
        string token = "rt-old";
        await using var watcher = new SignInWatcher(_ => { }, () => token);
        watcher.OnStatus(new() { LicenceUrl = server.Url });
        watcher.Start();
        await Until(() => server.WatchCalls >= 1, TimeSpan.FromSeconds(5));
        Check("first poll with the old sign-in", server.LastAuth, "Bearer rt-old");

        token = "rt-new";
        watcher.Wake();
        await Until(() => server.WatchCalls >= 2, TimeSpan.FromSeconds(3));
        Check("second poll started straight away", server.WatchCalls, 2);
        Check("with the new sign-in", server.LastAuth, "Bearer rt-new");

        // Status pushes once a second with the same server must not keep ending the poll.
        for (var i = 0; i < 5; i++) watcher.OnStatus(new() { LicenceUrl = server.Url });
        await Task.Delay(500);
        Check("same licence URL again does not restart it", server.WatchCalls, 2);
    }

    private static async Task StaleAnswerForAnOldSignInIsIgnored()
    {
        Section("watcher: an eviction of a sign-in no longer held is not shown");
        await using var server = new FakeServer();
        var release = new TaskCompletionSource();
        string token = "rt-old";
        server.WatchAsync = async ct =>
        {
            // Only the old sign-in was evicted; the new one is held like any live sign-in.
            var auth = server.LastAuth;
            if (auth != "Bearer rt-old")
            {
                await Task.Delay(TimeSpan.FromSeconds(45), ct);
                return (200, """{"state":"active","wait":0}""");
            }
            await release.Task.WaitAsync(ct);
            return (401, Evicted("MAY-NET", 1));
        };
        var fired = 0;
        await using var watcher = new SignInWatcher(_ => Interlocked.Increment(ref fired), () => token);
        watcher.OnStatus(new() { LicenceUrl = server.Url });
        watcher.Start();
        await Until(() => server.WatchCalls >= 1, TimeSpan.FromSeconds(5));

        token = "rt-new";       // signed out and in again while the poll was out, without a Wake
        release.SetResult();
        await Task.Delay(800);
        Check("not reported for the new sign-in", fired, 0);
    }

    private static async Task OutageBacksOffWithJitter()
    {
        Section("watcher: an outage retries after 1-10 s, then longer");
        await using var server = new FakeServer { Watch = _ => (503, "down") };
        await using var watcher = new SignInWatcher(_ => { }, () => "rt");
        watcher.OnStatus(new() { LicenceUrl = server.Url });
        watcher.Start();
        await Until(() => server.WatchCalls >= 1, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        Check("no retry inside the first second", server.WatchCalls, 1);
        await Until(() => server.WatchCalls >= 2, TimeSpan.FromSeconds(11));
        Check("retried within 10 s", server.WatchCalls >= 2, true);
    }

    private static async Task OldServerAndSignedOutBackOff()
    {
        Section("watcher: an old server (404) and a sign-in gone another way back off for minutes");
        foreach (var (status, body) in new[] { (404, "nope"), (401, """{"state":"signed_out","error":"x"}""") })
        {
            await using var server = new FakeServer { Watch = _ => (status, body) };
            var fired = 0;
            await using var watcher = new SignInWatcher(_ => fired++, () => "rt");
            watcher.OnStatus(new() { LicenceUrl = server.Url });
            watcher.Start();
            await Task.Delay(TimeSpan.FromSeconds(2));
            Check($"{status}: asked once in 2 s", server.WatchCalls, 1);
            Check($"{status}: nothing torn down", fired, 0);
        }
    }

    private static async Task NothingToWatchSendsNothing()
    {
        Section("watcher: not signed in, or self-hosted, sends nothing");
        await using var server = new FakeServer { Watch = _ => (200, """{"state":"active","wait":0}""") };
        await using (var signedOut = new SignInWatcher(_ => { }, () => null))
        {
            signedOut.OnStatus(new() { LicenceUrl = server.Url });
            signedOut.Start();
            await Task.Delay(700);
        }
        await using (var selfHosted = new SignInWatcher(_ => { }, () => "rt"))
        {
            selfHosted.OnStatus(new() { LicenceUrl = null });
            selfHosted.Start();
            await Task.Delay(700);
        }
        Check("no requests", server.WatchCalls, 0);
    }

    private static void ReportOnlyActsOnTheCode()
    {
        Section("Report: only signed_in_elsewhere");
        var fired = 0;
        var watcher = new SignInWatcher(_ => fired++, () => "rt");
        watcher.Report(new LicenceException("no plan", HttpStatusCode.PaymentRequired, "expired"));
        watcher.Report(new LicenceException("expired sign-in", HttpStatusCode.Unauthorized));
        Check("402 and a plain 401 ignored", fired, 0);
        watcher.Report(new LicenceExceptionProbe().Evicted());
        Check("signed_in_elsewhere reported", fired, 1);
    }

    private static void Message()
    {
        Section("the sentence");
        var at = new DateTimeOffset(2026, 10, 8, 21, 5, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 8)));
        var was = Loc.Current;
        try
        {
            SetLanguage(AppLanguage.Vietnamese);
            Check("vi, everything known", new SignedOutElsewhere("MAY-NET", at).Text(),
                "Tài khoản của bạn vừa đăng nhập trên máy khác (MAY-NET) lúc 21:05 08/10, nên máy này đã bị đăng xuất và ngắt kết nối. Đăng nhập lại để dùng tiếp trên máy này.");
            Check("vi, nothing known", new SignedOutElsewhere(null, null).Text(),
                "Tài khoản của bạn vừa đăng nhập trên máy khác, nên máy này đã bị đăng xuất và ngắt kết nối. Đăng nhập lại để dùng tiếp trên máy này.");
            SetLanguage(AppLanguage.English);
            Check("en", new SignedOutElsewhere("  DESKTOP-1 ", at).Text(),
                "Your account just signed in on another computer (DESKTOP-1) at 21:05 08/10, so this one was signed out and disconnected. Sign in again to keep using it here.");
        }
        finally
        {
            SetLanguage(was);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Loc.Set wants a running Avalonia app for its resources; Text() only reads the table, so swap that.</summary>
    private static void SetLanguage(AppLanguage language)
    {
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var table = language == AppLanguage.Vietnamese
            ? typeof(Loc).Assembly.GetType("GamePingBooster.App.Services.Localization.StringsVi")!
                .GetField("Values", any)!.GetValue(null)
            : typeof(Loc).GetField("English", any)!.GetValue(null);
        typeof(Loc).GetField("_current", any)!.SetValue(null, table);
        typeof(Loc).GetProperty(nameof(Loc.Current))!.SetValue(null, language);
    }

    private sealed class LicenceExceptionProbe
    {
        public LicenceException Evicted() => new("x", HttpStatusCode.Unauthorized, SignInWatcher.SignedInElsewhereCode);
    }

    private static string Evicted(string? label, long? at) =>
        $$"""{"state":"signed_in_elsewhere","code":"signed_in_elsewhere","error":"Tài khoản...","deviceLabel":{{(label is null ? "null" : $"\"{label}\"")}},"at":{{(at?.ToString() ?? "null")}}}""";

    private static async Task<Exception?> Catch(Func<Task> call)
    {
        try { await call(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static async Task Until(Func<bool> done, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed < limit) await Task.Delay(20);
    }

    private static void Section(string name) => Console.WriteLine($"\n{name}");

    private static void Check<T>(string name, T actual, T expected)
    {
        var ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (ok) _passes++; else _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {name}{(ok ? "" : $" - got {actual}, wanted {expected}")}");
    }

    /// <summary>A licence server of three routes on a loopback port, answering whatever the case sets.</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _watchCalls;

        public FakeServer()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }
        public int WatchCalls => Volatile.Read(ref _watchCalls);
        public string? LastAuth { get; private set; }
        public string? LastVersion { get; private set; }

        public Func<CancellationToken, Task<(int, string)>> WatchAsync { get; set; } = _ => Task.FromResult((200, """{"state":"active","wait":0}"""));

        /// <summary>An answer given at once.</summary>
        public Func<CancellationToken, (int, string)> Watch { set => WatchAsync = ct => Task.FromResult(value(ct)); }
        public (int, string) Token { get; set; } = (500, "");
        public (int, string) Account { get; set; } = (500, "");

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            (int status, string body) answer;
            try
            {
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/auth/watch")
                {
                    Interlocked.Increment(ref _watchCalls);
                    LastAuth = ctx.Request.Headers["Authorization"];
                    LastVersion = ctx.Request.Headers[LicenceClient.VersionHeader];
                    answer = await WatchAsync(_cts.Token);
                }
                else if (path == "/auth/token") answer = Token;
                else if (path == "/account") answer = Account;
                else answer = (404, "");

                var bytes = Encoding.UTF8.GetBytes(answer.body);
                ctx.Response.StatusCode = answer.status;
                ctx.Response.ContentType = answer.body.StartsWith('{') ? "application/json" : "text/html";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception)
            {
                try { ctx.Response.Abort(); } catch (Exception) { }
            }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Close();
            try { await _loop; } catch (Exception) { }
        }
    }
}


/// <summary>
/// Renders the sign-in window as it opens after this machine was signed out by another's sign-in, in both languages,
/// to PNGs - no server, no clicking:
///
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- signin-snap [outDir]
/// </summary>
internal static class SignInSnap
{
    public static int Run(string[] args)
    {
        var outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "gpb-signin-snaps");
        Directory.CreateDirectory(outDir);
        return Avalonia.AppBuilder.Configure(() => new SnapApp(outDir))
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime([]);
    }

    private sealed class SnapApp(string outDir) : Avalonia.Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        }

        /// <summary>Loc.Set would also save the choice into this PC's own settings; Apply only switches.</summary>
        private void Apply(AppLanguage language) =>
            typeof(Loc).GetMethod("Apply", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [this, language]);

        public override void OnFrameworkInitializationCompleted()
        {
            Loc.Initialize(this);
            if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    var code = 0;
                    var was = Loc.Current;
                    try
                    {
                        foreach (var language in new[] { AppLanguage.Vietnamese, AppLanguage.English })
                        {
                            Apply(language);
                            var at = DateTimeOffset.Now.AddMinutes(-1);
                            var vm = new App.ViewModels.LoginViewModel("https://gamepingbooster.com", "04" + new string('a', 128),
                                new PipeClient())
                            {
                                Notice = new SignedOutElsewhere("DESKTOP-NHA", at).Text(),
                            };
                            var window = new App.Views.LoginWindow
                            {
                                DataContext = vm,
                                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual,
                                Position = new Avalonia.PixelPoint(-4000, -4000),
                                ShowInTaskbar = false,
                            };
                            window.Show();
                            await Task.Delay(500);
                            var size = new Avalonia.PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                            bitmap.Render(window);
                            var name = $"signed-out-elsewhere-{(language == AppLanguage.Vietnamese ? "vi" : "en")}.png";
                            bitmap.Save(Path.Combine(outDir, name));
                            Console.WriteLine($"saved {Path.Combine(outDir, name)}");
                            window.Close();
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"snap failed: {ex}"); code = 1; }
                    finally { Apply(was); }
                    desktop.Shutdown(code);
                });
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}

/// <summary>
/// The app's half against a REAL licence server: real LicenceClient and SignInWatcher, real /auth/token,
/// /auth/watch and /account, two machines with real P-256 keys on one throwaway account.
///
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- signin-live <url> <tokens.json>
///
/// tokens.json is {"a": refresh token, "b": refresh token} for the SAME account, made on a local database.
/// </summary>
internal static class SignInLive
{
    private static int _failures;
    private static int _passes;

    public static int Run(string[] args)
    {
        var url = args[1];
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[2]));
        var a = doc.RootElement.GetProperty("a").GetString()!;
        var b = doc.RootElement.GetProperty("b").GetString()!;
        try { RunAsync(url, a, b).GetAwaiter().GetResult(); }
        catch (Exception ex) { _failures++; Console.WriteLine($"\nthe check itself failed: {ex}"); }
        Console.WriteLine(_failures == 0 ? $"\nall {_passes} checks passed" : $"\n{_failures} check(s) FAILED, {_passes} passed");
        return _failures == 0 ? 0 : 1;
    }

    private static string NewDeviceKey()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false).Q;
        return "04" + Convert.ToHexString(p.X!).ToLowerInvariant() + Convert.ToHexString(p.Y!).ToLowerInvariant();
    }

    private static async Task RunAsync(string url, string refreshA, string refreshB)
    {
        var keyA = NewDeviceKey();
        var keyB = NewDeviceKey();
        using var client = new LicenceClient(url);

        Console.WriteLine("\nmachine A signs in and is watched");
        var tokenA = await client.FetchTokenAsync(refreshA, keyA, "MAY-NHA", default);
        Check("A gets a licence token", tokenA.Token.Length, 300);

        var firedA = new System.Collections.Concurrent.ConcurrentQueue<(SignedOutElsewhere What, DateTimeOffset When)>();
        string? heldA = refreshA;
        await using var watchA = new SignInWatcher(w => firedA.Enqueue((w, DateTimeOffset.UtcNow)), () => heldA);
        watchA.OnStatus(new() { LicenceUrl = url });
        watchA.Start();
        await Task.Delay(2000);
        Check("nothing reported while nothing happens", firedA.Count, 0);

        Console.WriteLine("\nmachine B signs in on the same account");
        var signInB = DateTimeOffset.UtcNow;
        var tokenB = await client.FetchTokenAsync(refreshB, keyB, "MAY-NET", default);
        Check("B gets a licence token", tokenB.Token.Length, 300);

        var sw = Stopwatch.StartNew();
        while (firedA.IsEmpty && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Check("A's watcher reports it", firedA.Count, 1);
        if (firedA.TryPeek(out var fired))
        {
            var after = fired.When - signInB;
            Console.WriteLine($"        reported {after.TotalMilliseconds:F0} ms after B's sign-in");
            Check("within 2 s", after < TimeSpan.FromSeconds(2), true);
            Check("names B", fired.What.OtherDevice, "MAY-NET");
            Check("with the moment (within 5 s of B's sign-in)",
                fired.What.At is { } at && (at - signInB).Duration() < TimeSpan.FromSeconds(5), true);
            Console.WriteLine($"        \"{fired.What.Text()}\"");
        }

        Console.WriteLine("\nwhat A meets afterwards (an app that was off)");
        var renew = await Catch(() => client.FetchTokenAsync(refreshA, keyA, "MAY-NHA", default)) as LicenceException;
        Check("A's renewal is refused as signed_in_elsewhere", renew?.SignedInElsewhere, true);
        Check("naming B", renew?.OtherDevice, "MAY-NET");
        var account = await Catch(() => client.FetchAccountAsync(refreshA, default)) as LicenceException;
        Check("/account says the same", account?.SignedInElsewhere, true);

        var lateA = new System.Collections.Concurrent.ConcurrentQueue<SignedOutElsewhere>();
        await using (var fresh = new SignInWatcher(lateA.Enqueue, () => refreshA))
        {
            fresh.OnStatus(new() { LicenceUrl = url });
            fresh.Start();
            var t = Stopwatch.StartNew();
            while (lateA.IsEmpty && t.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Check("an app started later hears it on its first poll", lateA.Count, 1);
        }

        Console.WriteLine("\nmachine B is left alone");
        var firedB = 0;
        await using (var watchB = new SignInWatcher(_ => Interlocked.Increment(ref firedB), () => refreshB))
        {
            watchB.OnStatus(new() { LicenceUrl = url });
            watchB.Start();
            await Task.Delay(3000);
        }
        Check("B is not reported", firedB, 0);
        var renewB = await client.FetchTokenAsync(refreshB, keyB, "MAY-NET", default);
        Check("B renews normally", renewB.Token.Length, 300);
        var accountB = await client.FetchAccountAsync(refreshB, default);
        Check("B's account: 1 of 1 machines", (accountB.DeviceCount, accountB.DeviceLimit), (1, 1));
    }

    private static async Task<Exception?> Catch(Func<Task> call)
    {
        try { await call(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static void Check<T>(string name, T actual, T expected)
    {
        var ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (ok) _passes++; else _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {name}{(ok ? "" : $" - got {actual}, wanted {expected}")}");
    }
}
