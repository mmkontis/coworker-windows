using Coworker.Controls;
using Coworker.Models;
using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Views;

/// The "Customize Shortcuts" flyout: click a shortcut box, then press the new combination.
public static class ShortcutsFlyout
{
    /// <param name="onChanged">Called after any shortcut changed (to refresh labels).</param>
    public static void Show(FrameworkElement anchor, Action onChanged)
    {
        var settings = AppSettings.Instance;

        void Apply(Action change)
        {
            change();
            settings.Save();
            onChanged();
            GlobalHotkeyService.Instance?.RegisterAllHotkeys();
        }

        var panel = new StackPanel { Spacing = 16, MinWidth = 300, Padding = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = "Customize Shortcuts",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Click a shortcut box, then press your desired key combination.",
            FontSize = 12,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var dictation = new ShortcutRecorderButton(settings.DictationShortcut, allowModifierOnly: true, "Dictation shortcut");
        dictation.Recorded += shortcut => Apply(() => settings.DictationShortcut = shortcut);
        var clipboard = new ShortcutRecorderButton(settings.ClipboardShortcut, allowModifierOnly: false, "Clipboard shortcut");
        clipboard.Recorded += shortcut => Apply(() => settings.ClipboardShortcut = shortcut);

        panel.Children.Add(CreateRow("Dictation", "Starts transcription while you hold the shortcut", dictation));
        panel.Children.Add(CreateRow("Clipboard", "Opens clipboard history", clipboard));

        var reset = new Button
        {
            Content = "Reset to Defaults",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        reset.Click += (_, _) =>
        {
            Apply(() =>
            {
                settings.DictationShortcut = AppSettings.DefaultDictationShortcut;
                settings.ClipboardShortcut = AppSettings.DefaultClipboardShortcut;
            });
            dictation.Shortcut = settings.DictationShortcut;
            clipboard.Shortcut = settings.ClipboardShortcut;
        };
        panel.Children.Add(reset);

        new Flyout { Content = panel }.ShowAt(anchor);
    }

    private static Grid CreateRow(string title, string subtitle, ShortcutRecorderButton recorder)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
        });
        labels.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        row.Children.Add(labels);

        Grid.SetColumn(recorder, 1);
        row.Children.Add(recorder);
        return row;
    }
}
