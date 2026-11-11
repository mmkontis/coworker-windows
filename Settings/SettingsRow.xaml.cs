using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Coworker.Settings;

/// Title (14 medium) and optional subtitle (12 secondary) on the left, the control on the right
/// (Mac: SettingsRow / SettingsPickerRow).
[ContentProperty(Name = nameof(Trailing))]
public sealed partial class SettingsRow : UserControl
{
    public SettingsRow()
    {
        SettingsResources.EnsureLoaded();
        InitializeComponent();
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Subtitle
    {
        get => SubtitleText.Text;
        set
        {
            SubtitleText.Text = value;
            SubtitleText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public UIElement? Trailing
    {
        get => TrailingHost.Content as UIElement;
        set => TrailingHost.Content = value;
    }
}
