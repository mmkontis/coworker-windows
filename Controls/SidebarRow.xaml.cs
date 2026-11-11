using Coworker.Helpers;
using Coworker.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Coworker.Controls;

/// A navigation row of the sidebar (see SidebarRow.xaml). The window shell decides which row is selected.
public sealed partial class SidebarRow : UserControl
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    private bool _isSelected;

    public SidebarRow(SidebarItem item)
    {
        InitializeComponent();
        Item = item;

        IconSecondary.Child = Icon(item.Tab, "SidebarSecondaryText");
        IconPrimary.Child = Icon(item.Tab, "SidebarPrimaryText");
        LabelSecondary.Text = item.Title;
        LabelPrimary.Text = item.Title;
        AutomationProperties.SetName(RowButton, item.Title);
        AutomationProperties.SetAutomationId(RowButton, $"nav-{item.Tab}");

        foreach (var fading in new UIElement[] { SelectedFill, IconPrimary, LabelPrimary, Capsule })
            fading.OpacityTransition = new ScalarTransition { Duration = FadeDuration };
        IconSecondary.OpacityTransition = new ScalarTransition { Duration = FadeDuration };
        LabelSecondary.OpacityTransition = new ScalarTransition { Duration = FadeDuration };
    }

    public SidebarItem Item { get; }

    public event Action<AppTab>? Invoked;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;

            var on = value ? 1.0 : 0.0;
            SelectedFill.Opacity = on;
            IconPrimary.Opacity = on;
            LabelPrimary.Opacity = on;
            Capsule.Opacity = on;
            IconSecondary.Opacity = 1 - on;
            LabelSecondary.Opacity = 1 - on;
            AutomationProperties.SetItemStatus(RowButton, value ? "Selected" : "");
        }
    }

    private static IconElement Icon(AppTab tab, string brushKey)
    {
        var icon = SidebarIcons.For(tab);
        icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brushKey];
        return icon;
    }

    private void OnClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(Item.Tab);
}
