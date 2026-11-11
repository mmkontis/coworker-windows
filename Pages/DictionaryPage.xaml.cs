using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Pages;

/// The user's dictionary, saved on the server (/coworker/words). Every add, edit and delete goes to the
/// server first; the list changes only when the server accepted it, and the transcription cache follows
/// through <see cref="DictionaryChangedMessage"/>.
public sealed partial class DictionaryPage : Page
{
    private List<DictionaryEntry> _entries = new();
    private string _searchText = "";
    private bool _addFormVisible;

    /// The entry the form is editing, or null when it adds a new one.
    private DictionaryEntry? _editing;
    private bool _saving;

    public DictionaryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadEntries();
    }

    private async void LoadEntries()
    {
        ShowState("loading");
        try
        {
            _entries = await ApiService.Shared.GetDictionaryEntries();
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateSubtitle();
                RenderEntries();
            });
        }
        catch (Exception ex)
        {
            DebugLogWrite($"Dictionary not loaded: {ex.Message}");
            DispatcherQueue.TryEnqueue(() => RenderEntries());
            ToastService.Show(ToastKind.Error, null, "Couldn't load your dictionary. Check your connection and try again.", 6);
        }
    }

    private static void DebugLogWrite(string message) => Coworker.Helpers.DebugLog.Write(message);

    /// Tells the app state (and with it the words sent with every take) that the dictionary changed.
    private static void AnnounceChange() => WeakReferenceMessenger.Default.Send(new DictionaryChangedMessage());

    private void UpdateSubtitle()
    {
        SubtitleText.Text = _entries.Count == 0
            ? "Custom words and replacements for cleaner dictation."
            : $"{_entries.Count} custom entries ready for transcription and text expansion.";
    }

    private void RenderEntries()
    {
        var filtered = string.IsNullOrEmpty(_searchText)
            ? _entries
            : _entries.Where(e =>
                e.Word.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                e.Definition.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                (e.Shortcut?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        ContentPanel.Children.Clear();

        if (filtered.Count == 0 && _entries.Count == 0)
        {
            ShowState("empty");
            return;
        }

        ShowState("content");

        // Section header
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 20, 24, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "Dictionary Entries", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        header.Children.Add(new TextBlock
        {
            Text = $"({filtered.Count})", FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        });
        ContentPanel.Children.Add(header);

        foreach (var entry in filtered)
        {
            ContentPanel.Children.Add(CreateEntryRow(entry));
        }
    }

    private UIElement CreateEntryRow(DictionaryEntry entry)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(12), Margin = new Thickness(24, 0, 24, 8),
            Padding = new Thickness(16, 10, 16, 10),
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 0x24, 0x73, 0xF2)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0x24, 0x73, 0xF2)),
            BorderThickness = new Thickness(1)
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: word + type badge + definition
        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var wordRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        wordRow.Children.Add(new TextBlock
        {
            Text = entry.Word, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var badge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 2, 5, 2),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x80, 0x80, 0x80))
        };
        badge.Child = new TextBlock
        {
            Text = entry.TypeLabel, FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        wordRow.Children.Add(badge);
        left.Children.Add(wordRow);

        if (!string.IsNullOrEmpty(entry.Definition) && entry.Definition != entry.Word)
        {
            left.Children.Add(new TextBlock
            {
                Text = entry.Definition, FontSize = 12, MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        // Middle: shortcut
        if (!string.IsNullOrEmpty(entry.Shortcut))
        {
            var shortcutPill = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0x24, 0x73, 0xF2)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x24, 0x73, 0xF2)),
                BorderThickness = new Thickness(0.5), VerticalAlignment = VerticalAlignment.Center
            };
            var shortcutContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            shortcutContent.Children.Add(new FontIcon
            {
                Glyph = "\uE72A", FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0x24, 0x73, 0xF2))
            });
            shortcutContent.Children.Add(new TextBlock
            {
                Text = entry.Shortcut, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = (Brush)Application.Current.Resources["CwAccentBrush"],
                MaxLines = 1, MaxWidth = 200
            });
            shortcutPill.Child = shortcutContent;
            Grid.SetColumn(shortcutPill, 1);
            row.Children.Add(shortcutPill);
        }

        // Right: actions (on hover)
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var editBtn = new Button
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x24, 0x73, 0xF2))
        };
        editBtn.Content = new FontIcon { Glyph = "\uE70F", FontSize = 12, Foreground = (Brush)Application.Current.Resources["CwAccentBrush"] };
        ToolTipService.SetToolTip(editBtn, "Edit");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editBtn, $"Edit {entry.Word}");
        editBtn.Click += (_, _) => ShowForm(entry);

        var deleteBtn = new Button
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0x00, 0x00))
        };
        deleteBtn.Content = new FontIcon { Glyph = "\uE74D", FontSize = 12, Foreground = new SolidColorBrush(Colors.Red) };
        ToolTipService.SetToolTip(deleteBtn, "Delete");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(deleteBtn, $"Delete {entry.Word}");
        deleteBtn.Click += async (_, _) =>
        {
            // The row goes only once the server has deleted it; otherwise it stays and the user is told.
            deleteBtn.IsEnabled = false;
            try
            {
                await ApiService.Shared.DeleteDictionaryEntry(entry.Id);
            }
            catch (Exception ex)
            {
                DebugLogWrite($"Dictionary delete failed: {ex.Message}");
                deleteBtn.IsEnabled = true;
                ToastService.Show(ToastKind.Error, null, "Couldn't delete. Check your connection and try again.", 6);
                return;
            }
            _entries.Remove(entry);
            if (_editing == entry) HideForm();
            AnnounceChange();
            UpdateSubtitle();
            RenderEntries();
        };

        actions.Children.Add(editBtn);
        actions.Children.Add(deleteBtn);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        card.Child = row;

        card.PointerEntered += (_, _) => actions.Visibility = Visibility.Visible;
        card.PointerExited += (_, _) => actions.Visibility = Visibility.Collapsed;

        return card;
    }

    private void ShowState(string state)
    {
        ContentPanel.Visibility = state == "content" ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = state == "empty" ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = state == "loading" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleAddForm(object sender, RoutedEventArgs e)
    {
        if (_addFormVisible) HideForm();
        else ShowForm(null);
    }

    /// Opens the form to add a new entry (<paramref name="entry"/> null) or to edit one.
    private void ShowForm(DictionaryEntry? entry)
    {
        _editing = entry;
        _addFormVisible = true;
        AddFormPanel.Visibility = Visibility.Visible;
        AddIcon.Glyph = "\uE711";
        AddText.Text = "Cancel";
        NewWordBox.Text = entry?.Word ?? "";
        NewShortcutBox.Text = entry?.Shortcut ?? "";
        NewWordBox.Focus(FocusState.Programmatic);
        NewWordBox.SelectionStart = NewWordBox.Text.Length;
    }

    private void HideForm()
    {
        _editing = null;
        _addFormVisible = false;
        AddFormPanel.Visibility = Visibility.Collapsed;
        AddIcon.Glyph = "\uE710";
        AddText.Text = "Add Entry";
    }

    private async void OnSaveEntry(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        var word = NewWordBox.Text.Trim();
        if (string.IsNullOrEmpty(word)) return;
        var shortcut = NewShortcutBox.Text.Trim();
        var editing = _editing;

        _saving = true;
        try
        {
            if (editing == null)
            {
                await ApiService.Shared.AddDictionaryEntry(word, word, string.IsNullOrEmpty(shortcut) ? null : shortcut);
            }
            else
            {
                // A definition that only repeated the word follows the new word; a real one is kept.
                var definition = string.IsNullOrEmpty(editing.Definition) || editing.Definition == editing.Word ? word : editing.Definition;
                await ApiService.Shared.UpdateDictionaryEntry(editing.Id, word, definition, string.IsNullOrEmpty(shortcut) ? null : shortcut);
            }
        }
        catch (Exception ex)
        {
            DebugLogWrite($"Dictionary save failed: {ex.Message}");
            ToastService.Show(ToastKind.Error, null, "Couldn't save. Check your connection and try again.", 6);
            return;
        }
        finally
        {
            _saving = false;
        }

        HideForm();
        AnnounceChange();
        ToastService.Show(ToastKind.Success, null, editing == null ? "Added to dictionary" : "Dictionary entry updated");
        LoadEntries();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadEntries();

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        ClearSearch.Visibility = string.IsNullOrEmpty(_searchText) ? Visibility.Collapsed : Visibility.Visible;
        RenderEntries();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _searchText = "";
        ClearSearch.Visibility = Visibility.Collapsed;
        RenderEntries();
    }
}
