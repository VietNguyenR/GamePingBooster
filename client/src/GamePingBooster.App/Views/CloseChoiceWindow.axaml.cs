using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.Services;

namespace GamePingBooster.App.Views;

/// <summary>What the person chose in <see cref="CloseChoiceWindow"/>, and whether to stop asking.</summary>
public sealed record CloseChoice(CloseAction Action, bool Remember);

/// <summary>
/// Asked when the person closes the main window: to the tray, or quit. Closing this window itself -
/// its own X, Escape - answers null, and the main window stays as it was.
/// </summary>
public partial class CloseChoiceWindow : SurfaceWindow
{
    public CloseChoiceWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private bool Remember => this.FindControl<CheckBox>("RememberBox")?.IsChecked == true;

    private void OnTrayClick(object? sender, RoutedEventArgs e) => Close(new CloseChoice(CloseAction.Tray, Remember));

    private void OnQuitClick(object? sender, RoutedEventArgs e) => Close(new CloseChoice(CloseAction.Quit, Remember));
}
