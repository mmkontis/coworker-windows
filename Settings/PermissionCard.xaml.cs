using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Settings;

/// One row of Settings > Permissions: icon, title, description, a status dot with its label and,
/// unless access is granted, a pill button (Mac: PermissionCardAppView).
public sealed partial class PermissionCard : UserControl
{
    private static readonly Windows.UI.Color Green = Windows.UI.Color.FromArgb(255, 52, 199, 89);
    private static readonly Windows.UI.Color Red = Windows.UI.Color.FromArgb(255, 255, 59, 48);
    private static readonly Windows.UI.Color Orange = Windows.UI.Color.FromArgb(255, 255, 149, 0);

    public PermissionCard()
    {
        SettingsResources.EnsureLoaded();
        InitializeComponent();
    }

    /// The user pressed the pill button ("Enable" or "Open Settings").
    public event Action? ActionRequested;

    public string Glyph
    {
        get => IconGlyph.Glyph;
        set => IconGlyph.Glyph = value;
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Description
    {
        get => DescriptionText.Text;
        set => DescriptionText.Text = value;
    }

    public void SetAccess(MicrophoneAccess access)
    {
        var (text, color, action) = access switch
        {
            MicrophoneAccess.Granted => ("Granted", Green, (string?)null),
            MicrophoneAccess.Denied => ("Denied", Red, "Open Settings"),
            MicrophoneAccess.Restricted => ("Restricted", Red, "Open Settings"),
            _ => ("Not Granted", Orange, "Enable"),
        };

        var brush = new SolidColorBrush(color);
        StatusText.Text = text;
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
        ActionButton.Background = brush;
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
            ActionButton.Resources[key] = brush;
        ActionButton.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        ActionText.Text = action ?? "";
        AutomationProperties.SetName(ActionButton, action == null ? "" : $"{action}, {Title}");
        AutomationProperties.SetItemStatus(this, text);
    }

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionRequested?.Invoke();
}
