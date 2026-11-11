using Coworker.Models;
using Coworker.Services.VoiceCommands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Controls;

/// Chooses one action of the catalog (Mac: SearchableActionPicker). The list narrows as you type; the
/// query is cleared when the flyout closes.
public sealed partial class ActionPicker : UserControl
{
    private IReadOnlyList<ActionCatalogEntry> _catalog = Array.Empty<ActionCatalogEntry>();
    private string _selectedId = "";

    /// Raised when the user picks an action.
    public event Action<ActionCatalogEntry>? Selected;

    public ActionPicker()
    {
        InitializeComponent();
        RefreshTrigger();
    }

    public IReadOnlyList<ActionCatalogEntry> Catalog
    {
        get => _catalog;
        set
        {
            _catalog = value;
            RefreshTrigger();
        }
    }

    public string SelectedId
    {
        get => _selectedId;
        set
        {
            _selectedId = value;
            RefreshTrigger();
        }
    }

    // Filter: name, description or id contains the query, ignoring case.
    private IEnumerable<ActionCatalogEntry> Filtered()
    {
        var query = SearchBox.Text.Trim();
        return query.Length == 0
            ? _catalog
            : _catalog.Where(entry =>
                entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Id.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RefreshTrigger()
    {
        var entry = _catalog.FirstOrDefault(e => e.Id == _selectedId);
        var hasEntry = entry != null;
        EntryIcon.Visibility = EntryText.Visibility = hasEntry ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderIcon.Visibility = PlaceholderText.Visibility = hasEntry ? Visibility.Collapsed : Visibility.Visible;
        if (entry == null) return;

        EntryIcon.Glyph = CommandIcons.Glyph(entry.IconName);
        EntryIcon.Foreground = new SolidColorBrush(CommandIcons.TryParseColor(entry.IconColor) ?? Windows.UI.Color.FromArgb(0xFF, 0x24, 0x73, 0xF2));
        EntryText.Text = entry.Name;
    }

    private void RefreshOptions()
    {
        var entries = Filtered().ToList();
        var hasEntries = entries.Count > 0;
        ListScroll.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = $"No actions match \"{SearchBox.Text}\"";
        OptionsRepeater.ItemsSource = entries.Select((entry, i) => new ActionOptionViewModel(entry, entry.Id == _selectedId, i == 0)).ToList();
    }

    private void OnFlyoutOpened(object? sender, object e)
    {
        TriggerButton.BorderBrush = (Brush)Resources["CommandAccentStrokeOpen"];
        RefreshOptions();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnFlyoutClosed(object? sender, object e)
    {
        TriggerButton.BorderBrush = (Brush)Resources["CommandAccentStroke"];
        SearchBox.Text = "";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ClearButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshOptions();
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => SearchBox.Text = "";

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        var entry = ((ActionOptionViewModel)((FrameworkElement)sender).Tag).Entry;
        SelectedId = entry.Id;
        PickerFlyout.Hide();
        Selected?.Invoke(entry);
    }
}
