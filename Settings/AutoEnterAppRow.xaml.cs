using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Coworker.Settings;

/// An app in the Auto-Enter list: icon, name, how many clipboard items came from it, and the switch.
public sealed partial class AutoEnterAppRow : UserControl
{
    private readonly string _appName;

    public AutoEnterAppRow(AutoEnterAppOption option)
    {
        SettingsResources.EnsureLoaded();
        InitializeComponent();

        _appName = option.AppName;
        NameText.Text = option.AppName;
        UsageText.Text = option.UsageText;
        EnabledToggle.IsOn = option.IsEnabled;
        AutomationProperties.SetName(EnabledToggle, $"Auto-Enter in {option.AppName}");
        Loaded += OnLoaded;
    }

    /// The user switched the app on or off.
    public event Action<string, bool>? EnabledChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (await AppIconResolver.GetIconAsync(_appName) is not { } icon) return;

        AppIcon.Source = icon;
        AppIcon.Visibility = Visibility.Visible;
        PlaceholderIcon.Visibility = Visibility.Collapsed;
    }

    private void OnToggled(object sender, RoutedEventArgs e) => EnabledChanged?.Invoke(_appName, EnabledToggle.IsOn);
}
