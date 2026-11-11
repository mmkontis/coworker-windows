using Coworker.Models;
using Coworker.Services;
using Coworker.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Coworker.Views;

/// <summary>
/// The voice shortcuts, one mode per row, opened from the shortcut card above the account in the
/// sidebar: Dictation, Chat completion, and Clipboard history when that feature is on. Each row records
/// its shortcut in place (holds such as "Ctrl+Shift" for the voice modes, modifier + key for the
/// clipboard). Two modes can't share a shortcut: the one being recorded says so and keeps listening.
/// </summary>
public static class ShortcutsDialog
{
    private static readonly Color DictationColor = Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF);   // the bar's blue
    private static readonly Color ChatColor = Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07);        // the bar's chat yellow
    private static readonly Color ClipboardColor = Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93);

    // Segoe Fluent Icons: microphone, chat bubble, clipboard.
    private const string MicGlyph = "";
    private const string ChatGlyph = "";
    private const string ClipboardGlyph = "";

    private static bool _open;

    public static async Task ShowAsync(XamlRoot root)
    {
        if (_open) return;   // a second click while it is up would throw (one ContentDialog at a time)
        _open = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Shortcuts",
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Close,
                Content = BuildContent(),
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }

    private static UIElement BuildContent()
    {
        var settings = AppSettings.Instance;
        var rows = new StackPanel { Spacing = 10, MinWidth = 420 };

        rows.Children.Add(new TextBlock
        {
            Text = "Hold a shortcut to talk, let go to finish. Click a box to change it: press a combo, or hold modifiers on their own (Ctrl, Ctrl + Shift) and let go.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var dictation = Recorder(settings.DictationShortcut, allowModifierOnly: true, "Dictation shortcut");
        var chat = Recorder(settings.ChatShortcut, allowModifierOnly: true, "Chat completion shortcut");
        var clipboard = Recorder(settings.ClipboardShortcut, allowModifierOnly: false, "Clipboard shortcut");

        // Every other mode's shortcut, to refuse a duplicate.
        string? Clash(ShortcutRecorder self, string candidate)
        {
            if (self != dictation && Same(candidate, settings.DictationShortcut)) return "Dictation";
            if (self != chat && Same(candidate, settings.ChatShortcut)) return "Chat completion";
            if (self != clipboard && settings.ClipboardHistoryEnabled && Same(candidate, settings.ClipboardShortcut)) return "Clipboard";
            return null;
        }

        void Wire(ShortcutRecorder recorder, Action<string> store, Func<string> current)
        {
            recorder.ShortcutRecorded += shortcut =>
            {
                if (Clash(recorder, shortcut) is { } owner)
                {
                    recorder.Shortcut = current();
                    recorder.ShowMessage($"Already used by {owner}");
                    return;
                }
                store(shortcut);
                settings.Save();
                GlobalHotkeyService.Instance?.RegisterAllHotkeys();
            };
        }

        Wire(dictation, value => settings.DictationShortcut = value, () => settings.DictationShortcut);
        Wire(chat, value => settings.ChatShortcut = value, () => settings.ChatShortcut);
        Wire(clipboard, value => settings.ClipboardShortcut = value, () => settings.ClipboardShortcut);

        rows.Children.Add(Row(MicGlyph, DictationColor, "Dictation", "Speak, and your words are typed where you are", dictation));
        rows.Children.Add(Row(ChatGlyph, ChatColor, "Chat completion", "Ask out loud, and the AI's answer is pasted", chat));
        if (settings.ClipboardHistoryEnabled)
            rows.Children.Add(Row(ClipboardGlyph, ClipboardColor, "Clipboard history", "Opens everything you copied recently", clipboard));

        var reset = new HyperlinkButton { Content = "Reset to defaults", Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) };
        reset.Click += (_, _) =>
        {
            settings.DictationShortcut = AppSettings.DefaultDictationShortcut;
            settings.ChatShortcut = AppSettings.DefaultChatShortcut;
            settings.ClipboardShortcut = AppSettings.DefaultClipboardShortcut;
            settings.Save();
            GlobalHotkeyService.Instance?.RegisterAllHotkeys();
            dictation.Shortcut = settings.DictationShortcut;
            chat.Shortcut = settings.ChatShortcut;
            clipboard.Shortcut = settings.ClipboardShortcut;
        };
        rows.Children.Add(reset);
        return rows;
    }

    private static ShortcutRecorder Recorder(string shortcut, bool allowModifierOnly, string name)
    {
        var recorder = new ShortcutRecorder { Shortcut = shortcut, AllowModifierOnly = allowModifierOnly, MinWidth = 150 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(recorder, name);
        return recorder;
    }

    private static bool Same(string a, string b) =>
        HotkeyShortcut.TryParse(a, out var x) && HotkeyShortcut.TryParse(b, out var y) && x == y;

    private static Border Row(string glyph, Color color, string title, string subtitle, ShortcutRecorder recorder)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)) });
        badge.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(badge);

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        labels.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        recorder.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(recorder, 2);
        grid.Children.Add(recorder);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
    }
}
