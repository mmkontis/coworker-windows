using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Coworker.Settings;

/// A titled glass card: uppercase 13 pt bold header above a rounded-16 card that holds the rows
/// (Mac: SettingsSection in Components.swift).
[ContentProperty(Name = nameof(Body))]
public sealed partial class SettingsSection : UserControl
{
    public SettingsSection()
    {
        SettingsResources.EnsureLoaded();
        InitializeComponent();
    }

    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value.ToUpperInvariant();
            AutomationProperties.SetName(this, value);
        }
    }

    public UIElement? Body
    {
        get => BodyHost.Content as UIElement;
        set => BodyHost.Content = value;
    }
}
