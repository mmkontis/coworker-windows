using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Coworker.Controls;

/// The upgrade window's body (see UpgradeCard.xaml). "connect" in the target address makes it the
/// sign-in prompt, anything else the plan upgrade (Mac UpgradeWindowView).
public sealed partial class UpgradeCard : UserControl
{
    public const double CardWidth = 320;

    public UpgradeCard(string message, string upgradeUrl, int? currentUsage, int? limit)
    {
        InitializeComponent();

        var isConnect = upgradeUrl.Contains("connect", StringComparison.Ordinal);
        TitleText.Text = isConnect ? "Sign In Required" : "Upgrade Your Plan";
        ActionText.Text = isConnect ? "Sign In" : "Upgrade";
        AutomationProperties.SetName(ActionButton, ActionText.Text);
        MessageText.Text = message;

        if (currentUsage is { } usage && limit is { } max)
        {
            UsageText.Text = $"{usage}/{max}";
            UsageRow.Visibility = Visibility.Visible;
        }
    }

    public event Action? NotNow;
    public event Action? ActionInvoked;

    private void OnNotNow(object sender, RoutedEventArgs e) => NotNow?.Invoke();

    private void OnAction(object sender, RoutedEventArgs e) => ActionInvoked?.Invoke();
}
