using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;

namespace GamePingBooster.PurchaseCheck;

/// <summary>
/// Checks buying a plan in the app, end to end, against a LOCAL web-service dev server.
///
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- logic [http://localhost:5183]
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- ui    [http://localhost:5183]
///     dotnet run --project client/src/GamePingBooster.PurchaseCheck -- snap  [http://localhost:5183] [outDir]
///
/// `logic` drives the real LicenceClient, PurchaseWatcher and UpgradeViewModel - the code the app
/// runs - over real HTTP, and makes the transfer land the way SePay does: a signed webhook to the
/// server, sent by web-service's `npm run dev:app-checkout -- pay`. Accounts come from the same
/// script (`create`), which refuses any database that is not on this machine.
///
/// `ui` opens the upgrade window on a throwaway account, for looking at.
///
/// Never touches this PC's own sign-in: every credential here is a throwaway account's, handed to
/// the view models directly, and RefreshTokenStore is not read or written.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _passes;
    internal static string LicenceUrl = "http://localhost:5183";

    private static readonly string WebService = Environment.GetEnvironmentVariable("GPB_WEB_SERVICE")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "..", "web-service"));

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1) LicenceUrl = args[1];
        if (args.FirstOrDefault() == "ui") return UiCheck.Run(args);
        if (args.FirstOrDefault() == "snap") return SnapCheck.Run(args);

        // Faster than in the app, so a run takes seconds rather than minutes. Same code paths.
        PurchaseWatcher.PollInterval = TimeSpan.FromMilliseconds(300);
        PurchaseWatcher.SlowAfter = TimeSpan.FromMinutes(5);

        try
        {
            RunAll().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"\nthe check itself failed: {ex}");
        }
        finally
        {
            Node("cleanup");
        }

        Console.WriteLine(_failures == 0 ? $"\nall {_passes} checks passed" : $"\n{_failures} check(s) FAILED, {_passes} passed");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunAll()
    {
        Node("cleanup");
        await Formatting();
        await PlansAndChoosing();
        await ExpiredTrialPaysAndGetsALicence();
        await ExpiredSignInOpensThePlans();
        await LiveTrialTokenMovesAtOnce();
        await ClosingTheWindowDoesNotStopTheUpgrade();
        await ReopeningShowsTheSameOrder();
        await CancelGoesBackToPlans();
        await CancelLosesToAPaymentThatLanded();
        await WrongAmount();
        await ForgedWebhookChangesNothing();
        await RefusedAccounts();
        await Referral();
        await ServerDownWhileChoosing();
        await ServerDownWhileWaiting();
        await SignInRevokedWhileWaiting();
        await SignedOutWhileWaiting();
        await GivesUpAfterTheLimit();
        await RateLimit();
    }

    // ------------------------------------------------------------------ cases

    private static Task Formatting()
    {
        Section("money is printed the Vietnamese way");
        Check("49000 -> 49.000đ", UpgradeViewModel.Money(49000), "49.000đ");
        Check("389000 -> 389.000đ", UpgradeViewModel.Money(389000), "389.000đ");
        return Task.CompletedTask;
    }

    private static async Task PlansAndChoosing()
    {
        Section("plans: loaded from the server, length keeps the tier");
        var acct = Create("plans", "--trial-hours", "5");
        using var client = new LicenceClient(LicenceUrl);
        var plans = await client.FetchPlansAsync(acct.Token, "vi", default);
        Check("purchasable", plans.Purchasable, true);
        Check("current plan is the trial", plans.CurrentPlanCode, "trial");
        Check("there are plans", plans.Groups.Sum(g => g.Plans.Count) > 0, true);
        Check("no free plan", plans.Groups.SelectMany(g => g.Plans).Any(p => p.PriceMinor <= 0), false);

        var (vm, watcher, _) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        Check("screen: choose", vm.Current, UpgradeViewModel.Screen.Choose);
        Check("cheapest plan preselected", vm.Selected?.Code, plans.Groups[0].Plans[0].Code);
        Check("total shows its price", vm.TotalText, UpgradeViewModel.Money(plans.Groups[0].Plans[0].AmountMinor));

        var dualMonthly = plans.Groups[0].Plans.FirstOrDefault(p => p.Tier != plans.Groups[0].Plans[0].Tier);
        var longer = plans.Groups.FirstOrDefault(g => g.Months > 1);
        if (dualMonthly is not null && longer is not null)
        {
            vm.PickPlan(dualMonthly.Code);
            vm.PickMonths(longer.Months);
            Check("changing length keeps the tier", vm.Selected?.Tier, dualMonthly.Tier);
            Check("and selects that length's plan", vm.Selected?.Code, longer.Plans.First(p => p.Tier == dualMonthly.Tier).Code);
            Check("tabs mark the chosen length", vm.Tabs.Single(t => t.IsSelected).Months, longer.Months);
        }
        vm.PickPlan("no-such-plan");
        Check("an unknown code changes nothing", vm.Selected is not null, true);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task ExpiredTrialPaysAndGetsALicence()
    {
        Section("purchase: expired trial -> pay in the app -> licence with the same sign-in");
        var acct = Create("expired", "--expired");
        var key = DeviceKey();
        using var client = new LicenceClient(LicenceUrl);

        var refused = await Catch(() => client.FetchTokenAsync(acct.Token, key, "check", default));
        Check("before: licence refused with 402", refused?.StatusCode, HttpStatusCode.PaymentRequired);

        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        Check("screen: pay", vm.Current, UpgradeViewModel.Screen.Pay);
        Check("invoice shown", vm.InvoiceNumber.StartsWith("GPB"), true);
        Check("memo is the invoice", vm.Memo, vm.InvoiceNumber);
        Check("account number shown", vm.AccountNumber.Length > 5, true);
        Check("watcher is watching", watcher.Watching, true);
        Check("not renewed yet", paid.Count, 0);

        var hook = Pay(vm.InvoiceNumber);
        Check("webhook accepted", hook.Contains("\"status\":200") && hook.Contains("applied"), true);
        await WaitFor(() => vm.IsPaid, "the screen to turn paid");

        Check("screen: paid", vm.Current, UpgradeViewModel.Screen.Paid);
        Check("Purchased set for the account screen", vm.Purchased, true);
        Check("paid text names the plan", vm.PaidText.Contains(vm.PlanName), true);
        Check("licence renewal triggered exactly once", paid.Count, 1);
        Check("watcher stopped", watcher.Watching, false);

        var token = await client.FetchTokenAsync(acct.Token, key, "check", default);
        Check("after: the SAME refresh token gets a licence", token.Token.Length > 0, true);
        var account = await client.FetchAccountAsync(acct.Token, default);
        Check("account shows the paid plan as active", account.Status, "ACTIVE");
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task ExpiredSignInOpensThePlans()
    {
        Section("sign-in to an expired account: signed in, no licence, plans open with the reason");
        var acct = Create("expired-signin", "--expired");
        var key = DeviceKey();
        using var client = new LicenceClient(LicenceUrl);

        var refused = await Catch(() => client.FetchTokenAsync(acct.Token, key, "check", default));
        Check("the server codes the refusal", refused?.Code, "expired");
        Check("which reads as the app's own sentence", refused is null ? null : UpgradeViewModel.NoticeFor(refused),
            GamePingBooster.App.Services.Localization.Loc.T("upgrade.expired"));
        Check("a refusal from a server without codes reads the same",
            UpgradeViewModel.NoticeFor(new LicenceException("server text", HttpStatusCode.PaymentRequired)),
            GamePingBooster.App.Services.Localization.Loc.T("upgrade.expired"));
        Check("an uncoded-for reason keeps the server's sentence",
            UpgradeViewModel.NoticeFor(new LicenceException("verify first", HttpStatusCode.PaymentRequired, "unverified")),
            "verify first");

        // A PipeClient that is never started: on a 402 nothing is sent to the service.
        string? saved = null;
        var login = new LoginViewModel(LicenceUrl, key, new PipeClient(), saveRefreshToken: t => saved = t);
        await login.FinishWithRefreshTokenAsync(acct.Token, default);
        Check("the sign-in counts as done", login.Succeeded, true);
        Check("and is not shown as an error", login.HasError, false);
        Check("the refresh token is kept", saved, acct.Token);
        Check("the plans are asked for, with the reason", login.NeedsPlan, GamePingBooster.App.Services.Localization.Loc.T("upgrade.expired"));

        var (vm, watcher, paid) = NewVm(acct.Token);
        var plans = new UpgradeViewModel(LicenceUrl, watcher, () => acct.Token) { Notice = login.NeedsPlan };
        Check("the notice shows while loading", plans.HasNotice, true);
        await plans.LoadAsync(default);
        Check("the kept token loads the plans", plans.Current, UpgradeViewModel.Screen.Choose);
        Check("the notice shows over the plans", plans.HasNotice, true);

        Check("terms not ticked: Pay is off", plans.CanPay, false);
        await plans.PayAsync(default);
        Check("and pressing it anyway makes no order", plans.Current, UpgradeViewModel.Screen.Choose);
        Check("terms link is the site's page", plans.TermsUrl, new Uri(LicenceUrl).GetLeftPart(UriPartial.Authority) + "/terms-of-service");
        Check("refund link is the site's page", plans.RefundUrl, new Uri(LicenceUrl).GetLeftPart(UriPartial.Authority) + "/refund-policy");
        plans.Agreed = true;
        Check("ticked: Pay is on", plans.CanPay, true);
        await plans.PayAsync(default);
        Check("and is gone once there is an order", plans.HasNotice, false);

        Pay(plans.InvoiceNumber);
        await WaitFor(() => plans.IsPaid, "the screen to turn paid");
        var token = await client.FetchTokenAsync(acct.Token, key, "check", default);
        Check("paid: the kept refresh token now gets a licence", token.Token.Length > 0, true);
        plans.Detach();
        vm.Detach();
        watcher.Dispose();
        Check("renewal triggered once", paid.Count, 1);
    }

    private static async Task LiveTrialTokenMovesAtOnce()
    {
        Section("purchase: during a live trial the next token runs past the trial");
        var acct = Create("live", "--trial-hours", "2");
        var key = DeviceKey();
        using var client = new LicenceClient(LicenceUrl);
        var before = await client.FetchTokenAsync(acct.Token, key, "check", default);
        Check("trial token ends within ~2h", before.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= 2 * 3600 + 60, true);

        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        Pay(vm.InvoiceNumber);
        await WaitFor(() => paid.Count == 1, "the renewal callback");
        var after = await client.FetchTokenAsync(acct.Token, key, "check", default);
        Check("renewed token outlives the trial by hours", after.ExpiresAt - before.ExpiresAt > 3600, true);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task ClosingTheWindowDoesNotStopTheUpgrade()
    {
        Section("closing the window: the watcher still upgrades the machine");
        var acct = Create("closed", "--trial-hours", "1");
        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        var invoice = vm.InvoiceNumber;
        vm.Detach(); // what UpgradeWindow.Closed does

        Pay(invoice);
        await WaitFor(() => paid.Count == 1, "the renewal callback with the window closed");
        Check("renewed once", paid.Count, 1);
        Check("watcher holds the paid order", watcher.Current?.Status, "PAID");
        watcher.Dispose();
    }

    private static async Task ReopeningShowsTheSameOrder()
    {
        Section("reopening the window while an order is open shows that order");
        var acct = Create("reopen", "--trial-hours", "1");
        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        var invoice = vm.InvoiceNumber;
        vm.Detach();

        var again = new UpgradeViewModel(LicenceUrl, watcher, () => acct.Token) { Agreed = true };
        await again.LoadAsync(default);
        Check("straight to the pay screen", again.Current, UpgradeViewModel.Screen.Pay);
        Check("same invoice", again.InvoiceNumber, invoice);

        // Pressing pay again from the plan list would reuse it on the server too.
        using var client = new LicenceClient(LicenceUrl);
        var reused = await client.CreateOrderAsync(acct.Token, watcher.Current!.Plan!.Code, "vi", default);
        Check("the server reuses the open order", reused.Order?.InvoiceNumber, invoice);
        Check("and says so", reused.Reused, true);

        Pay(invoice);
        await WaitFor(() => again.IsPaid, "the reopened screen to turn paid");
        Check("renewed once", paid.Count, 1);
        again.Detach();
        watcher.Dispose();
    }

    private static async Task CancelGoesBackToPlans()
    {
        Section("cancel: order closed on the server, back to the plan list");
        var acct = Create("cancel", "--trial-hours", "1");
        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        var invoice = vm.InvoiceNumber;
        await vm.CancelOrderAsync(default);
        Check("screen: choose", vm.Current, UpgradeViewModel.Screen.Choose);
        Check("watcher stopped", watcher.Watching, false);
        Check("watcher forgot it", watcher.Current is null, true);

        using var client = new LicenceClient(LicenceUrl);
        var order = await client.FetchOrderAsync(acct.Token, invoice, "vi", default);
        Check("server: CANCELLED", order.Order?.Status, "CANCELLED");
        Check("no transfer details for a cancelled order", order.Order?.Transfer is null, true);

        var late = Pay(invoice);
        Check("money after cancel is not applied", late.Contains("not_payable"), true);
        Check("no renewal", paid.Count, 0);

        await vm.PayAsync(default);
        Check("paying again mints a new order", vm.InvoiceNumber != invoice, true);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task CancelLosesToAPaymentThatLanded()
    {
        Section("cancel pressed after the money landed: it is paid, not cancelled");
        var acct = Create("cancelrace", "--trial-hours", "1");
        PurchaseWatcher.PollInterval = TimeSpan.FromSeconds(30); // so the watcher cannot notice first
        try
        {
            var (vm, watcher, paid) = NewVm(acct.Token);
            await vm.LoadAsync(default);
            await vm.PayAsync(default);
            Pay(vm.InvoiceNumber);
            await vm.CancelOrderAsync(default);
            Check("screen: paid", vm.Current, UpgradeViewModel.Screen.Paid);
            Check("renewed once", paid.Count, 1);
            vm.Detach();
            watcher.Dispose();
        }
        finally
        {
            PurchaseWatcher.PollInterval = TimeSpan.FromMilliseconds(300);
        }
    }

    private static async Task WrongAmount()
    {
        Section("wrong amount: flagged, still open, then completed by the right one");
        var acct = Create("amount", "--trial-hours", "1");
        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        var amount = long.Parse(vm.AmountDigits);

        Pay(vm.InvoiceNumber, (amount - 1000).ToString());
        await WaitFor(() => vm.AmountMismatch, "the mismatch to show");
        Check("still on the pay screen", vm.Current, UpgradeViewModel.Screen.Pay);
        Check("mismatch sentence names both amounts", vm.MismatchText.Contains(UpgradeViewModel.Money(amount - 1000)) && vm.MismatchText.Contains(UpgradeViewModel.Money(amount)), true);
        Check("no renewal", paid.Count, 0);

        Pay(vm.InvoiceNumber, amount.ToString());
        await WaitFor(() => vm.IsPaid, "the right amount to complete it");
        Check("renewed once", paid.Count, 1);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task ForgedWebhookChangesNothing()
    {
        Section("forged webhook: refused, screen keeps waiting");
        var acct = Create("forged", "--trial-hours", "1");
        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        var forged = Pay(vm.InvoiceNumber, null, "--bad-signature");
        Check("server refuses it (401)", forged.Contains("\"status\":401"), true);
        var otherAccount = Pay(vm.InvoiceNumber, null, "--account", "9999999999");
        Check("money into another account is ignored", otherAccount.Contains("ignored"), true);
        await Task.Delay(1500);
        Check("still pending", vm.Current, UpgradeViewModel.Screen.Pay);
        Check("no renewal", paid.Count, 0);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task RefusedAccounts()
    {
        Section("refused accounts: creator and suspended see why, nothing is created");
        foreach (var (label, flag, expect) in new[] { ("creator", "--creator", "creator"), ("suspended", "--suspended", "suspended") })
        {
            var acct = Create(label, flag);
            var (vm, watcher, _) = NewVm(acct.Token);
            await vm.LoadAsync(default);
            Check($"{label}: blocked screen", vm.Current, UpgradeViewModel.Screen.Blocked);
            Check($"{label}: says why", vm.Message.Length > 10, true);

            using var client = new LicenceClient(LicenceUrl);
            var refused = await Catch(() => client.CreateOrderAsync(acct.Token, "standard", "vi", default));
            Check($"{label}: a direct order is 403", refused?.StatusCode, HttpStatusCode.Forbidden);
            Check($"{label}: with the reason code", refused?.Code, expect);
            vm.Detach();
            watcher.Dispose();
        }
    }

    private static async Task Referral()
    {
        Section("referral: discounted price shown and charged");
        var acct = Create("referral", "--trial-hours", "1", "--referral", "10");
        var (vm, watcher, _) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        Check("discount line shown", vm.HasDiscount, true);
        Check("card shows the struck-through price", vm.Selected?.HasStrike, true);
        var shown = vm.Selected!.AmountMinor;
        await vm.PayAsync(default);
        Check("order is for the shown amount", long.Parse(vm.AmountDigits), shown);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task ServerDownWhileChoosing()
    {
        Section("server unreachable while loading: failed screen with a reason, retry works");
        var acct = Create("down", "--trial-hours", "1");
        var url = "http://127.0.0.1:59999";
        var watcher = new PurchaseWatcher(() => acct.Token, () => { }, a => a());
        var vm = new UpgradeViewModel(url, watcher, () => acct.Token, _ => new LicenceClient(url)) { Agreed = true };
        await vm.LoadAsync(default);
        Check("failed screen", vm.Current, UpgradeViewModel.Screen.Failed);
        Check("with a reason", vm.Message.Length > 10, true);

        var vm2 = new UpgradeViewModel(LicenceUrl, watcher, () => acct.Token) { Agreed = true };
        await vm2.StartOverAsync(default);
        Check("retry against a live server: choose", vm2.Current, UpgradeViewModel.Screen.Choose);
        vm.Detach();
        vm2.Detach();
        watcher.Dispose();
    }

    private static async Task ServerDownWhileWaiting()
    {
        Section("server unreachable while waiting: says so, keeps watching, completes when it is back");
        var acct = Create("flaky", "--trial-hours", "1");
        var down = false;
        var paid = new List<int>();
        var watcher = new PurchaseWatcher(() => acct.Token, () => paid.Add(1), a => a(),
            _ => new LicenceClient(down ? "http://127.0.0.1:59999" : LicenceUrl));
        var vm = new UpgradeViewModel(LicenceUrl, watcher, () => acct.Token) { Agreed = true };
        await vm.LoadAsync(default);
        await vm.PayAsync(default);

        var initial = vm.WatchText;
        down = true;
        await WaitFor(() => vm.WatchText != initial, "the reconnecting message", 15);
        Check("still on the pay screen", vm.Current, UpgradeViewModel.Screen.Pay);
        Check("still watching", watcher.Watching, true);

        Pay(vm.InvoiceNumber);
        down = false;
        await WaitFor(() => vm.IsPaid, "paid once the server is back");
        Check("renewed once", paid.Count, 1);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task SignInRevokedWhileWaiting()
    {
        Section("sign-in revoked while waiting: watching stops with the reason");
        var acct = Create("revoked", "--trial-hours", "1");
        var (vm, watcher, paid) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        using (var client = new LicenceClient(LicenceUrl)) await client.LogoutAsync(acct.Token, default);
        await WaitFor(() => !watcher.Watching, "the watcher to stop");
        Check("the screen says it stopped", vm.WatchText.Length > 0 && vm.WatchText != GamePingBooster.App.Services.Localization.Loc.T("upgrade.pay.waiting"), true);
        Check("no renewal", paid.Count, 0);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task SignedOutWhileWaiting()
    {
        Section("signed out on this machine while waiting: watching stops");
        var acct = Create("signedout", "--trial-hours", "1");
        string? token = acct.Token;
        var paid = new List<int>();
        var watcher = new PurchaseWatcher(() => token, () => paid.Add(1), a => a());
        var vm = new UpgradeViewModel(LicenceUrl, watcher, () => token) { Agreed = true };
        await vm.LoadAsync(default);
        await vm.PayAsync(default);
        token = null;
        await WaitFor(() => !watcher.Watching, "the watcher to stop");
        Check("no renewal", paid.Count, 0);
        vm.Detach();
        watcher.Dispose();
    }

    private static async Task GivesUpAfterTheLimit()
    {
        Section("nobody pays: the watcher gives up and says the order stays open");
        var acct = Create("giveup", "--trial-hours", "1");
        var saved = PurchaseWatcher.GiveUpAfter;
        PurchaseWatcher.GiveUpAfter = TimeSpan.FromSeconds(2);
        try
        {
            var (vm, watcher, paid) = NewVm(acct.Token);
            await vm.LoadAsync(default);
            await vm.PayAsync(default);
            await WaitFor(() => !watcher.Watching, "the watcher to give up");
            Check("the screen says so", vm.WatchText, GamePingBooster.App.Services.Localization.Loc.T("upgrade.pay.gaveUp"));
            Check("still the pay screen (order open)", vm.Current, UpgradeViewModel.Screen.Pay);
            vm.Detach();
            watcher.Dispose();
        }
        finally
        {
            PurchaseWatcher.GiveUpAfter = saved;
        }
    }

    private static async Task RateLimit()
    {
        Section("rate limit: order after order is refused with a readable reason");
        var acct = Create("ratelimit", "--trial-hours", "1");
        var (vm, watcher, _) = NewVm(acct.Token);
        await vm.LoadAsync(default);
        for (var i = 0; i < 10; i++)
        {
            await vm.PayAsync(default);
            await vm.CancelOrderAsync(default);
        }
        await vm.PayAsync(default);
        Check("still on the plan list", vm.Current, UpgradeViewModel.Screen.Choose);
        Check("with the rate-limit sentence", vm.Error, GamePingBooster.App.Services.Localization.Loc.T("upgrade.err.rateLimited"));
        vm.Detach();
        watcher.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private static (UpgradeViewModel vm, PurchaseWatcher watcher, List<int> paid) NewVm(string token)
    {
        var paid = new List<int>();
        var watcher = new PurchaseWatcher(() => token, () => { lock (paid) paid.Add(1); }, a => a());
        return (new UpgradeViewModel(LicenceUrl, watcher, () => token) { Agreed = true }, watcher, paid);
    }

    internal sealed record Account(string UserId, string Email, string Token);

    internal static Account Create(string label, params string[] flags)
    {
        var json = Node(["create", label, .. flags]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new Account(root.GetProperty("userId").GetString()!, root.GetProperty("email").GetString()!,
            root.GetProperty("refreshToken").GetString()!);
    }

    internal static string Pay(string invoice, string? amount = null, params string[] flags)
    {
        var port = new Uri(LicenceUrl).Port.ToString();
        string[] args = amount is null ? ["pay", invoice, "--port", port, .. flags] : ["pay", invoice, amount, "--port", port, .. flags];
        return Node(args);
    }

    private static string Node(params string[] args)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = WebService,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("npm");
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("dev:app-checkout");
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"dev:app-checkout {string.Join(' ', args)} failed: {error}{output}");
        return output.Trim().Split('\n').Last().Trim();
    }

    private static string DeviceKey() => "04" + Convert.ToHexString(RandomNumberGenerator.GetBytes(64)).ToLowerInvariant();

    private static async Task<LicenceException?> Catch(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (LicenceException ex)
        {
            return ex;
        }
    }

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        _failures++;
        Console.WriteLine($"  FAIL  timed out waiting for {what}");
    }

    private static void Section(string name) => Console.WriteLine($"\n{name}");

    private static void Check<T>(string name, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected))
        {
            _passes++;
            Console.WriteLine($"  ok    {name}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL  {name}\n          expected {expected}\n          got      {actual}");
        }
    }
}
