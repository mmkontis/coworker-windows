using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Controls;

/// The 200-wide option list of the tray panel's Language rows (Mac MenuPickerRow): a search field when
/// there are more than ten options, rows of 12 pt text, the selected one in the accent colour with a check.
internal static class OptionPickerFlyout
{
    private const double RowHeight = 31;

    public static Flyout Create(IReadOnlyList<(string Code, string Name)> options, string selected, Action<string> onSelect)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Left,
            ShouldConstrainToRootBounds = false,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["PopoverFlyoutPresenterStyle"],
        };

        var rows = new StackPanel { Spacing = 2 };
        void Fill(string filter)
        {
            rows.Children.Clear();
            foreach (var (code, name) in options.Where(o => filter.Length == 0 || o.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            {
                var selectedRow = code == selected;
                var content = new Grid { Padding = new Thickness(9, 7, 9, 7), ColumnSpacing = 6 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.Children.Add(new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                if (selectedRow)
                {
                    var check = new FontIcon { Glyph = "", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    Grid.SetColumn(check, 1);
                    content.Children.Add(check);
                }

                var button = new Button
                {
                    Content = content,
                    CornerRadius = new CornerRadius(4),
                    Style = (Style)Application.Current.Resources["PopoverRowButtonStyle"],
                };
                if (selectedRow) button.Foreground = (Brush)Application.Current.Resources["CwAccentBrush"];
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
                button.Click += (_, _) =>
                {
                    flyout.Hide();
                    onSelect(code);
                };
                rows.Children.Add(button);
            }
        }
        Fill("");

        var panel = new StackPanel { Width = 200, Padding = new Thickness(7), Spacing = 5 };
        if (options.Count > 10)
        {
            var search = new TextBox { PlaceholderText = "Search languages" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search languages");
            search.TextChanged += (_, _) => Fill(search.Text.Trim());
            panel.Children.Add(search);
            flyout.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        }
        panel.Children.Add(new ScrollViewer
        {
            Height = Math.Min(240, options.Count * RowHeight),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rows,
        });
        flyout.Content = panel;
        return flyout;
    }
}
