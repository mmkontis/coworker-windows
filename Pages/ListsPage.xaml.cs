using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Coworker.Models;

namespace Coworker.Pages;

public sealed partial class ListsPage : Page
{
    private readonly List<ListItem> _items = new();

    public ListsPage()
    {
        InitializeComponent();
        RefreshList();
    }

    private void RefreshList()
    {
        var viewModels = _items
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new ListItemViewModel(
                i.Id, i.Title, i.Description, i.IsCompleted,
                i.CreatedAt.ToString("MMM d, yyyy"),
                i.IsCompleted ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
                string.IsNullOrEmpty(i.Description) ? Visibility.Collapsed : Visibility.Visible))
            .ToList();

        ListRepeater.ItemsSource = viewModels;

        var completed = _items.Count(i => i.IsCompleted);
        SummaryText.Text = _items.Count > 0
            ? $"{completed} of {_items.Count} completed"
            : "";

        var hasItems = _items.Count > 0;
        MainContent.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAddItem(object sender, RoutedEventArgs e)
    {
        var title = NewTitleBox.Text.Trim();
        if (string.IsNullOrEmpty(title)) return;

        _items.Add(new ListItem
        {
            Title = title,
            Description = NewDescriptionBox.Text.Trim()
        });

        NewTitleBox.Text = "";
        NewDescriptionBox.Text = "";
        RefreshList();
    }

    private void OnToggleItem(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string id)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                item.IsCompleted = cb.IsChecked == true;
                RefreshList();
            }
        }
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _items.RemoveAll(i => i.Id == id);
            RefreshList();
        }
    }

    private void OnClearCompleted(object sender, RoutedEventArgs e)
    {
        _items.RemoveAll(i => i.IsCompleted);
        RefreshList();
    }
}

public record ListItemViewModel(
    string Id, string Title, string Description, bool IsCompleted,
    string FormattedDate, Windows.UI.Text.TextDecorations StrikethroughDecoration,
    Visibility DescriptionVisibility);
