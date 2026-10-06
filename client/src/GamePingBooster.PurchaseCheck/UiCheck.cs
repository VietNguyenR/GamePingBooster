using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.App.ViewModels;
using GamePingBooster.App.Views;

namespace GamePingBooster.PurchaseCheck;

/// <summary>
/// Opens the real account window on a throwaway account (expired trial by default), so the upgrade
/// button and the upgrade window can be looked at and clicked through. Pay from a terminal with
/// `npm run dev:app-checkout -- pay &lt;invoice&gt;` in web-service and watch it turn paid.
///
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- ui [url] [create flags...]
/// </summary>
internal static class UiCheck
{
    public static int Run(string[] args)
    {
        var flags = args.Length > 2 ? args[2..] : ["--expired"];
        var account = Program.Create("ui", flags);
        Console.WriteLine($"account {account.Email}");
        return AppBuilder.Configure(() => new UiApp(account))
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime([]);
    }

    private sealed class UiApp(Program.Account account) : Application
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
                var watcher = new PurchaseWatcher(() => account.Token,
                    () => Console.WriteLine("renew requested (TokenRefresher.RenewNow in the app)"),
                    a => Dispatcher.UIThread.Post(a));
                watcher.Updated += u => Console.WriteLine($"order {u.Order?.InvoiceNumber} {u.Order?.Status} {u.Error}");

                // A PipeClient that is never started: the account window only uses it to sign out.
                var window = new AccountWindow
                {
                    DataContext = new AccountViewModel(Program.LicenceUrl, "04" + new string('a', 128), new PipeClient(),
                        watcher, () => account.Token),
                };
                desktop.MainWindow = window;
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
