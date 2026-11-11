using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using Coworker.Models;
using Coworker.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Coworker.Pages;

public sealed partial class HistoryPage : Page
{
    private List<TranscriptionHistoryItem> _items = new();
    private string _searchText = "";

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadHistory();
    }

    private async void LoadHistory()
    {
        ShowState("loading");

        try
        {
            var historyTask = ApiService.Shared.GetTranscriptionHistory();
            var statsTask = ApiService.Shared.GetDashboardStats();
            await Task.WhenAll(historyTask, statsTask);

            _items = historyTask.Result;
            var stats = statsTask.Result;

            DispatcherQueue.TryEnqueue(() =>
            {
                TranscriptionCount.Text = _items.Count.ToString("N0");
                WordCount.Text = stats.TotalWords > 0
                    ? stats.TotalWords.ToString("N0")
                    : _items.Sum(i => i.WordCount).ToString("N0");
                RenderItems();
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ErrorText.Text = ex.Message;
                ShowState("error");
            });
        }
    }

    private void RenderItems()
    {
        var filtered = string.IsNullOrEmpty(_searchText)
            ? _items
            : _items.Where(i =>
                i.Text.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                (i.App?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        ContentPanel.Children.Clear();

        if (filtered.Count == 0)
        {
            EmptyTitle.Text = string.IsNullOrEmpty(_searchText) ? "No Transcriptions Yet" : "No Results Found";
            EmptySubtitle.Text = string.IsNullOrEmpty(_searchText)
                ? "Your transcription history will appear here.\nStart recording to create your first transcription."
                : "Try a different search term.";
            ShowState("empty");
            return;
        }

        ShowState("content");

        foreach (var item in filtered)
        {
            ContentPanel.Children.Add(CreateHistoryCard(item));
        }
    }

    private UIElement CreateHistoryCard(TranscriptionHistoryItem item)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var content = new StackPanel { Spacing = 12 };

        // Header row
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerLeft.Children.Add(new TextBlock
        {
            Text = item.FormattedDate, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        if (!string.IsNullOrEmpty(item.App))
        {
            headerLeft.Children.Add(new TextBlock
            {
                Text = "·", Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
            });
            headerLeft.Children.Add(new TextBlock
            {
                Text = item.App, FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        Grid.SetColumn(headerLeft, 0);
        header.Children.Add(headerLeft);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        badges.Children.Add(new TextBlock
        {
            Text = $"{item.WordCount} words", FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        if (item.Duration > 0)
        {
            badges.Children.Add(new TextBlock
            {
                Text = item.FormattedDuration, FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        Grid.SetColumn(badges, 1);
        header.Children.Add(badges);

        content.Children.Add(header);

        // Text
        content.Children.Add(new TextBlock
        {
            Text = item.Text, FontSize = 14, MaxLines = 3,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Copy button
        var copyBtn = new Button { Content = "Copy", Visibility = Visibility.Collapsed };
        copyBtn.Click += (_, _) =>
        {
            var pkg = new DataPackage();
            pkg.SetText(item.Text);
            Clipboard.SetContent(pkg);
        };
        content.Children.Add(copyBtn);

        card.Child = content;

        card.PointerEntered += (_, _) =>
        {
            copyBtn.Visibility = Visibility.Visible;
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x24, 0x73, 0xF2));
        };
        card.PointerExited += (_, _) =>
        {
            copyBtn.Visibility = Visibility.Collapsed;
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        };

        return card;
    }

    private void ShowState(string state)
    {
        ContentPanel.Visibility = state == "content" ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = state == "loading" ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = state == "empty" ? Visibility.Visible : Visibility.Collapsed;
        ErrorState.Visibility = state == "error" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadHistory();

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        ClearSearch.Visibility = string.IsNullOrEmpty(_searchText) ? Visibility.Collapsed : Visibility.Visible;
        RenderItems();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _searchText = "";
        ClearSearch.Visibility = Visibility.Collapsed;
        RenderItems();
    }
}
