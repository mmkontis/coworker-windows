using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Pages;

public sealed partial class PlaybooksPage : Page
{
    private List<PlaybookItem> _allPlaybooks = new();
    private string? _editingId;

    public PlaybooksPage()
    {
        InitializeComponent();
        Loaded += (_, _) => FetchPlaybooks();
    }

    private async void FetchPlaybooks()
    {
        try
        {
            _allPlaybooks = await ApiService.Shared.GetPlaybooks();
        }
        catch { }
        RefreshList();
    }

    private void RefreshList()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? _allPlaybooks
            : _allPlaybooks.Where(p =>
                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
              .ToList();

        var viewModels = filtered
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new PlaybookViewModel(p.Id, p.Title, p.Category, p.Content,
                p.CreatedAt.ToString("MMM d, yyyy")))
            .ToList();

        PlaybooksRepeater.ItemsSource = viewModels;

        var hasItems = viewModels.Count > 0;
        MainContent.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void OnNewPlaybook(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        DialogTitle.Text = "New Playbook";
        DialogSaveText.Text = "Create";
        PlaybookTitleBox.Text = "";
        PlaybookCategoryBox.Text = "";
        PlaybookContentBox.Text = "";
        DialogOverlay.Visibility = Visibility.Visible;
    }

    private void OnEditPlaybook(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var item = _allPlaybooks.FirstOrDefault(p => p.Id == id);
            if (item == null) return;

            _editingId = id;
            DialogTitle.Text = "Edit Playbook";
            DialogSaveText.Text = "Save";
            PlaybookTitleBox.Text = item.Title;
            PlaybookCategoryBox.Text = item.Category;
            PlaybookContentBox.Text = item.Content;
            DialogOverlay.Visibility = Visibility.Visible;
        }
    }

    private async void OnDeletePlaybook(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            try { await ApiService.Shared.DeletePlaybook(id); } catch { }
            _allPlaybooks.RemoveAll(p => p.Id == id);
            RefreshList();
        }
    }

    private void OnDialogCancel(object sender, RoutedEventArgs e)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnDialogSave(object sender, RoutedEventArgs e)
    {
        var title = PlaybookTitleBox.Text.Trim();
        var category = PlaybookCategoryBox.Text.Trim();
        var content = PlaybookContentBox.Text.Trim();
        if (string.IsNullOrEmpty(title)) return;

        var cat = string.IsNullOrEmpty(category) ? "General" : category;

        if (_editingId != null)
        {
            try { await ApiService.Shared.UpdatePlaybook(_editingId, title, content, cat); } catch { }
            var existing = _allPlaybooks.FirstOrDefault(p => p.Id == _editingId);
            if (existing != null)
            {
                existing.Title = title;
                existing.Category = cat;
                existing.Content = content;
                existing.UpdatedAt = DateTime.Now;
            }
        }
        else
        {
            try
            {
                var created = await ApiService.Shared.CreatePlaybook(title, content, cat);
                if (created != null) _allPlaybooks.Add(created);
                else _allPlaybooks.Add(new PlaybookItem { Title = title, Category = cat, Content = content });
            }
            catch
            {
                _allPlaybooks.Add(new PlaybookItem { Title = title, Category = cat, Content = content });
            }
        }

        DialogOverlay.Visibility = Visibility.Collapsed;
        RefreshList();
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            var actions = FindDescendant<StackPanel>(border, "CardActions");
            if (actions != null) actions.Opacity = 1;
        }
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            var actions = FindDescendant<StackPanel>(border, "CardActions");
            if (actions != null) actions.Opacity = 0;
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name) return typed;
            var result = FindDescendant<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }
}

public record PlaybookViewModel(string Id, string Title, string Category, string Content, string FormattedDate);
