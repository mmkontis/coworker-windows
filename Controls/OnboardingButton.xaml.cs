using Coworker.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Coworker.Controls;

public sealed partial class OnboardingButton : UserControl
{
    private static readonly Color DeepBlue = Color.FromArgb(0xFF, 0x14, 0x29, 0x73);

    public OnboardingButton()
    {
        InitializeComponent();
        Apply();
        ElementShadow.Attach(ShadowHost, 10, Color.FromArgb(0x26, 0, 0, 0), 16, 4);
    }

    public event RoutedEventHandler? Click;

    public string Text
    {
        get => Label.Text;
        set
        {
            Label.Text = value;
            AutomationProperties.SetName(Root, value);
        }
    }

    /// White pill with a shadow (default) or the translucent "Skip" style. Set before the button is shown.
    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            _isPrimary = value;
            Apply();
            ShadowHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool _isPrimary = true;

    private void Apply()
    {
        var (fill, hover, text, weight) = _isPrimary
            ? (Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFF, 0xF2, 0xF4, 0xF8), DeepBlue, Microsoft.UI.Text.FontWeights.SemiBold)
            : (Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Microsoft.UI.Text.FontWeights.Medium);

        Root.Resources["ButtonBackground"] = new SolidColorBrush(fill);
        Root.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        Root.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(hover);
        Root.Resources["ButtonForeground"] = new SolidColorBrush(text);
        Root.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(text);
        Root.Resources["ButtonForegroundPressed"] = new SolidColorBrush(text);
        Root.Resources["ButtonBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        Root.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
        Root.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);
        Label.FontWeight = weight;
        Label.Foreground = new SolidColorBrush(text);
    }

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
