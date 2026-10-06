using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.ViewModels;

namespace GamePingBooster.App.Views;

/// <summary>
/// The upgrade screen. Everything it does is in <see cref="UpgradeViewModel"/>; this only forwards
/// clicks and the clipboard, which needs the window.
/// </summary>
public partial class UpgradeWindow : SurfaceWindow
{
    private readonly CancellationTokenSource _cts = new();

    public UpgradeWindow()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is UpgradeViewModel vm) await vm.LoadAsync(_cts.Token);
        };

        Closed += (_, _) =>
        {
            // Stops this window's requests, NOT the watching: an open order goes on being watched by
            // PurchaseWatcher, so paying after closing still upgrades this machine at once.
            _cts.Cancel();
            _cts.Dispose();
            if (DataContext is UpgradeViewModel vm) vm.Detach();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpgradeViewModel vm && (sender as Control)?.DataContext is MonthTab tab) vm.PickMonths(tab.Months);
    }

    private void OnCardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpgradeViewModel vm && (sender as Control)?.DataContext is PlanCard card) vm.PickPlan(card.Code);
    }

    private async void OnPayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpgradeViewModel vm) await vm.PayAsync(_cts.Token);
    }

    private async void OnCancelOrderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpgradeViewModel vm) await vm.CancelOrderAsync(_cts.Token);
    }

    private async void OnStartOverClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpgradeViewModel vm) await vm.StartOverAsync(_cts.Token);
    }

    private void OnCopyAmountClick(object? sender, RoutedEventArgs e) =>
        Copy((DataContext as UpgradeViewModel)?.AmountDigits);

    private void OnCopyAccountClick(object? sender, RoutedEventArgs e) => Copy((DataContext as UpgradeViewModel)?.AccountNumber);

    private void OnCopyMemoClick(object? sender, RoutedEventArgs e) => Copy((DataContext as UpgradeViewModel)?.Memo);

    private async void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Held open by another program. The text is on screen and selectable.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
