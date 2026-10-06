using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.App.ViewModels;
using GamePingBooster.App.Views;

namespace GamePingBooster.PurchaseCheck;

/// <summary>
/// Walks the real windows through a purchase and renders each state to a PNG, without anybody
/// clicking: account screen -> plans -> QR -> paid. The windows are placed off screen.
///
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- snap [url] [outDir]
/// </summary>
internal static class SnapCheck
{
    public static int Run(string[] args)
    {
        var outDir = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "gpb-purchase-snaps");
        Directory.CreateDirectory(outDir);
        var account = Program.Create("snap", "--expired");
        Console.WriteLine($"account {account.Email} -> {outDir}");
        return AppBuilder.Configure(() => new SnapApp(account, outDir))
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime([]);
    }

    private sealed class SnapApp(Program.Account account, string outDir) : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            Loc.Initialize(this);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(async () =>
                {
                    var code = 0;
                    try { await Walk(); }
                    catch (Exception ex) { Console.WriteLine($"snap failed: {ex}"); code = 1; }
                    desktop.Shutdown(code);
                });
            }
            base.OnFrameworkInitializationCompleted();
        }

        private async Task Walk()
        {
            var renewed = 0;
            var watcher = new PurchaseWatcher(() => account.Token, () => renewed++, a => Dispatcher.UIThread.Post(a));

            var accountVm = new AccountViewModel(Program.LicenceUrl, "04" + new string('a', 128), new PipeClient(), watcher, () => account.Token);
            var accountWindow = OffScreen(new AccountWindow { DataContext = accountVm });
            accountWindow.Show();
            await Until(() => accountVm.Ready);
            await Snap(accountWindow, "1-account.png");
            Console.WriteLine($"upgrade button visible: {accountVm.CanUpgrade}");

            var vm = new UpgradeViewModel(Program.LicenceUrl, watcher, () => account.Token);
            var window = OffScreen(new UpgradeWindow { DataContext = vm });
            window.Show();
            await Until(() => vm.IsChoose);
            await Snap(window, "2-plans.png");

            var longer = vm.Tabs.FirstOrDefault(t => t.Months == 3);
            if (longer is not null)
            {
                vm.PickMonths(3);
                await Snap(window, "3-plans-3-months.png");
                vm.PickMonths(1);
            }

            await vm.PayAsync(default);
            await Until(() => vm.HasQr || vm.QrMissing, 15);
            Console.WriteLine($"invoice {vm.InvoiceNumber}, qr loaded: {vm.HasQr}");
            await Snap(window, "4-pay.png");

            Console.WriteLine(Program.Pay(vm.InvoiceNumber));
            await Until(() => vm.IsPaid, 15);
            await Snap(window, "5-paid.png");
            Console.WriteLine($"renew requested {renewed} time(s)");

            window.Close();
            await accountVm.LoadAsync(default);
            await Snap(accountWindow, "6-account-after.png");
            Console.WriteLine($"account now: {accountVm.Plan} / {accountVm.Status} / {accountVm.Expires}");
            accountWindow.Close();
            watcher.Dispose();
        }

        private static T OffScreen<T>(T window) where T : Window
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(-4000, -4000);
            window.ShowInTaskbar = false;
            return window;
        }

        private async Task Snap(Window window, string name)
        {
            await Task.Delay(400); // let layout and bindings settle
            var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(window);
            bitmap.Save(Path.Combine(outDir, name));
            Console.WriteLine($"saved {name} ({size.Width}x{size.Height})");
        }

        private static async Task Until(Func<bool> condition, int seconds = 10)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
            if (!condition()) throw new TimeoutException("state not reached");
        }
    }
}
