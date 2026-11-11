using Coworker.Helpers;
using Coworker.Models;
using Coworker.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Coworker.Controls;

/// See SidebarHotkeyCard.xaml. Shows the dictation shortcut as key caps and follows the setting; a click
/// opens <see cref="ShortcutsDialog"/>, where every mode's shortcut is changed.
public sealed partial class SidebarHotkeyCard : UserControl
{
    public SidebarHotkeyCard()
    {
        InitializeComponent();
        // The card is a faint tint, and a SwiftUI shadow follows the view's alpha; a full-strength shadow would show through it.
        ElementShadow.Attach(CardShadow, 8, Color.FromArgb(0x10, 0, 0, 0), 2, 2);
        AppSettings.Changed += () => UiThread.Post(ShowCaps);
        ShowCaps();
    }

    private void ShowCaps()
    {
        Caps.Children.Clear();
        foreach (var token in AppSettings.Instance.DictationShortcut.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var cap = new Border
            {
                MinWidth = 28,
                Height = 24,
                Padding = new Thickness(7, 0, 7, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1, 1, 1, 2),   // a slightly deeper bottom edge reads as a key
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SidebarNeutralFill"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SidebarDivider"],
                Child = new TextBlock
                {
                    Text = token,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.9,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SidebarPrimaryText"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Caps.Children.Add(cap);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(CardButton,
            $"Dictation shortcut {AppSettings.Instance.DictationShortcut}. Activate to change shortcuts.");
    }

    private async void OnClick(object sender, RoutedEventArgs e) => await ShortcutsDialog.ShowAsync(XamlRoot);
}
