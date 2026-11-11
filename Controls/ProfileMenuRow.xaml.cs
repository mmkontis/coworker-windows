using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Controls;

/// One row of the profile popover (see ProfileMenuRow.xaml).
public sealed partial class ProfileMenuRow : UserControl
{
    private bool _isSelected;
    private bool _isDestructive;

    public ProfileMenuRow()
    {
        InitializeComponent();
        UpdateColors();
    }

    public event RoutedEventHandler? Click;

    public string Glyph
    {
        get => Icon.Glyph;
        set => Icon.Glyph = value;
    }

    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            AutomationProperties.SetName(RowButton, value);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            CheckIcon.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) RowButton.Background = (Brush)Application.Current.Resources["PopoverSelectedFill"];
            else RowButton.ClearValue(Control.BackgroundProperty);
            AutomationProperties.SetItemStatus(RowButton, value ? "Selected" : "");
            UpdateColors();
        }
    }

    /// Red text and icon (Log Out).
    public bool IsDestructive
    {
        get => _isDestructive;
        set
        {
            _isDestructive = value;
            UpdateColors();
        }
    }

    // Icon, title and check inherit the button's foreground; the primary text colour is its style default.
    private void UpdateColors()
    {
        if (_isDestructive) RowButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30));
        else if (_isSelected) RowButton.Foreground = (Brush)Application.Current.Resources["CwAccentBrush"];
        else RowButton.ClearValue(Control.ForegroundProperty);
    }

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
