using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI;
using Windows.UI;
using Coworker.Models;
using Coworker.Services;
using System.Collections.ObjectModel;

namespace Coworker.Pages;

public sealed partial class ChatPage : Page
{
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private bool _isTyping;
    private string _selectedModel = "gemini-3.1-flash-lite-preview";

    private CoworkerContexts? _contexts;
    private string _selectedWorkspaceId = "personal";
    private string _selectedCompanyId = "all";
    private bool _loadingContexts;

    public ChatPage()
    {
        InitializeComponent();
        LoadModels();
        LoadContexts();
    }

    private async void LoadModels()
    {
        var models = await ApiService.Shared.GetChatModels();
        DispatcherQueue.TryEnqueue(() =>
        {
            ModelSelector.Items.Clear();
            foreach (var (code, name) in models)
            {
                ModelSelector.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            }
            if (ModelSelector.Items.Count > 0) ModelSelector.SelectedIndex = 0;
        });
    }

    // MARK: - Workspace / company scope (company_users memberships)

    private async void LoadContexts()
    {
        CoworkerContexts? contexts = null;
        if (SessionService.Shared.IsAuthenticated)
        {
            try
            {
                contexts = await ApiService.Shared.FetchUserContexts();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatPage] FetchUserContexts failed: {ex.Message}");
            }
        }
        DispatcherQueue.TryEnqueue(() => PopulateContexts(contexts));
    }

    private void PopulateContexts(CoworkerContexts? contexts)
    {
        _loadingContexts = true;
        _contexts = contexts;

        // Workspaces — fall back to a single "Personal" workspace.
        var workspaces = contexts?.Workspaces is { Count: > 0 }
            ? contexts.Workspaces
            : new List<CoworkerWorkspace> { new() { Id = "personal", Name = "Personal", Kind = "personal" } };

        WorkspaceSelector.Items.Clear();
        foreach (var ws in workspaces)
            WorkspaceSelector.Items.Add(new ComboBoxItem { Content = ws.Name, Tag = ws.Id });
        SelectComboByTag(WorkspaceSelector, _selectedWorkspaceId, 0);
        _selectedWorkspaceId = (WorkspaceSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "personal";

        // Companies — "All companies" plus each company_users membership.
        CompanySelector.Items.Clear();
        CompanySelector.Items.Add(new ComboBoxItem { Content = "All companies", Tag = "all" });
        foreach (var c in contexts?.Companies ?? new List<CoworkerCompany>())
            CompanySelector.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });

        // Prefer the user's persisted pick, else the backend's active company.
        var desired = AppSettings.Instance.SelectedCompanyId;
        if (string.IsNullOrEmpty(desired) || desired == "all")
            desired = contexts?.ActiveCompanyId ?? "all";
        SelectComboByTag(CompanySelector, desired, 0);
        _selectedCompanyId = (CompanySelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        AppSettings.Instance.SelectedCompanyId = _selectedCompanyId;
        AppSettings.Instance.Save();

        _loadingContexts = false;
        UpdateScopeSubtitle();
    }

    private static void SelectComboByTag(ComboBox combo, string? tag, int fallbackIndex)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Tag as string) == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = fallbackIndex;
    }

    private void OnWorkspaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingContexts) return;
        _selectedWorkspaceId = (WorkspaceSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "personal";
        UpdateScopeSubtitle();
    }

    private void OnCompanyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingContexts) return;
        _selectedCompanyId = (CompanySelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        AppSettings.Instance.SelectedCompanyId = _selectedCompanyId;
        AppSettings.Instance.Save();
        UpdateScopeSubtitle();
    }

    private void UpdateScopeSubtitle()
    {
        var workspaceName = (WorkspaceSelector.SelectedItem as ComboBoxItem)?.Content as string ?? "Personal";
        string scope;
        if (string.IsNullOrEmpty(_selectedCompanyId) || _selectedCompanyId == "all")
        {
            scope = workspaceName;
        }
        else
        {
            var companyName = _contexts?.Companies.FirstOrDefault(c => c.Id == _selectedCompanyId)?.Name
                ?? (CompanySelector.SelectedItem as ComboBoxItem)?.Content as string;
            scope = string.IsNullOrEmpty(companyName) ? workspaceName : $"{workspaceName} · {companyName}";
        }
        ScopeSubtitle.Text = $"AI-powered conversation · {scope}";
    }

    private void UpdateVisibility()
    {
        EmptyState.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MessagesScroll.Visibility = _messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildMessages();
    }

    private void RebuildMessages()
    {
        var panel = new StackPanel { Spacing = 0 };
        foreach (var msg in _messages)
        {
            panel.Children.Add(CreateMessageRow(msg));
        }
        MessagesRepeater.ItemsSource = null;
        var sv = MessagesScroll;
        sv.Content = panel;
    }

    private UIElement CreateMessageRow(ChatMessage message)
    {
        var isUser = message.IsUser;
        var row = new Grid { Padding = new Thickness(32, 6, 32, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = isUser ? new GridLength(80) : GridLength.Auto
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = isUser ? GridLength.Auto : new GridLength(80)
        });

        // Avatar
        var avatar = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top };
        var avatarEllipse = new Ellipse();
        if (isUser)
        {
            avatarEllipse.Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            avatar.Children.Add(avatarEllipse);
            avatar.Children.Add(new FontIcon
            {
                Glyph = "\uE77B", FontSize = 14, Foreground = new SolidColorBrush(Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(avatar, 2);
        }
        else
        {
            var grad = new LinearGradientBrush();
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x26, 0x24, 0x73, 0xF2), Offset = 0 });
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x1A, 0x4D, 0xD9, 0xFF), Offset = 1 });
            avatarEllipse.Fill = grad;
            avatar.Children.Add(avatarEllipse);
            avatar.Children.Add(new FontIcon
            {
                Glyph = "\uE945", FontSize = 14,
                Foreground = (SolidColorBrush)Application.Current.Resources["CwAccentBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(avatar, 0);
        }
        row.Children.Add(avatar);

        // Bubble
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = isUser ? new Thickness(0, 0, 12, 0) : new Thickness(12, 0, 0, 0),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 600
        };

        if (isUser)
        {
            var grad = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x24, 0x73, 0xF2), Offset = 0 });
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xE6, 0x24, 0x73, 0xF2), Offset = 1 });
            bubble.Background = grad;
        }
        else
        {
            bubble.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        }

        var textBlock = new TextBlock
        {
            Text = message.Content,
            FontSize = 14.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            IsTextSelectionEnabled = true,
            Foreground = isUser ? new SolidColorBrush(Colors.White) : null
        };
        bubble.Child = textBlock;

        Grid.SetColumn(bubble, 1);
        row.Children.Add(bubble);

        return row;
    }

    private async void SendMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        MessageInput.Text = "";

        var userMsg = new ChatMessage { Role = "user", Content = text.Trim() };
        _messages.Add(userMsg);
        UpdateVisibility();

        var assistantMsg = new ChatMessage { Role = "assistant", Content = "" };
        _messages.Add(assistantMsg);
        _isTyping = true;

        try
        {
            await foreach (var delta in ApiService.Shared.ChatCompletionStream(text, _selectedModel, _messages.ToList()))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    assistantMsg.Content += delta;
                    UpdateVisibility();
                });
            }
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                assistantMsg.Content = $"Error: {ex.Message}";
                UpdateVisibility();
            });
        }

        _isTyping = false;
    }

    private void OnSend(object sender, RoutedEventArgs e) => SendMessage(MessageInput.Text);

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !_isTyping)
        {
            e.Handled = true;
            SendMessage(MessageInput.Text);
        }
    }

    private void OnNewChat(object sender, RoutedEventArgs e)
    {
        _messages.Clear();
        UpdateVisibility();
    }

    private void OnSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string prompt)
        {
            SendMessage(prompt);
        }
    }
}
