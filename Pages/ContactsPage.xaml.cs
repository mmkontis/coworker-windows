using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI;
using Windows.UI;
using Coworker.Models;

namespace Coworker.Pages;

public sealed partial class ContactsPage : Page
{
    private readonly List<ContactInfo> _contacts = new();
    private bool _showingCalls = false;
    private string _searchText = "";

    public ContactsPage()
    {
        InitializeComponent();
        UpdateTabSelection();
        RenderContacts();
    }

    private void UpdateTabSelection()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["CwAccentBrush"];
        var transparent = new SolidColorBrush(Colors.Transparent);

        CallsTab.Background = _showingCalls ? new SolidColorBrush(Color.FromArgb(0x1A, 0x24, 0x73, 0xF2)) : transparent;
        ContactsTab.Background = !_showingCalls ? new SolidColorBrush(Color.FromArgb(0x1A, 0x24, 0x73, 0xF2)) : transparent;
    }

    private void RenderContacts()
    {
        ContentPanel.Children.Clear();
        ContentPanel.Visibility = Visibility.Collapsed;
        CallsEmptyState.Visibility = Visibility.Collapsed;
        ContactsEmptyState.Visibility = Visibility.Collapsed;

        if (_showingCalls)
        {
            CallsEmptyState.Visibility = Visibility.Visible;
            return;
        }

        var filtered = string.IsNullOrEmpty(_searchText)
            ? _contacts
            : _contacts.Where(c => c.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            ContactsEmptyState.Visibility = Visibility.Visible;
            return;
        }

        ContentPanel.Visibility = Visibility.Visible;

        var grid = new StackPanel { Spacing = 16, Margin = new Thickness(24, 0, 24, 0) };
        foreach (var contact in filtered)
        {
            grid.Children.Add(CreateContactCard(contact));
        }
        ContentPanel.Children.Add(grid);
    }

    private UIElement CreateContactCard(ContactInfo contact)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(16),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xB3, 0xD9, 0xFF)),
            BorderThickness = new Thickness(0.5)
        };

        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Avatar
        var avatar = new Grid { Width = 48, Height = 48 };
        var avatarBg = new Ellipse();
        avatarBg.Fill = new LinearGradientBrush
        {
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x33, 0x24, 0x73, 0xF2), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x1A, 0x24, 0x73, 0xF2), Offset = 1 }
            }
        };
        avatar.Children.Add(avatarBg);
        avatar.Children.Add(new TextBlock
        {
            Text = contact.Initials, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (SolidColorBrush)Application.Current.Resources["CwAccentBrush"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(avatar, 0);
        row.Children.Add(avatar);

        // Info
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = contact.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1
        });
        info.Children.Add(new TextBlock
        {
            Text = contact.HasPhoneNumber ? contact.PhoneNumbers.First() : "No phone number",
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources[contact.HasPhoneNumber ? "TextFillColorSecondaryBrush" : "TextFillColorTertiaryBrush"],
            FontStyle = contact.HasPhoneNumber ? Windows.UI.Text.FontStyle.Normal : Windows.UI.Text.FontStyle.Italic
        });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        // Call button
        var callBtn = new Button
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
            Padding = new Thickness(0), Visibility = Visibility.Collapsed,
            Background = contact.HasPhoneNumber
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x34, 0xC7, 0x59))
                : new SolidColorBrush(Color.FromArgb(0x59, 0x80, 0x80, 0x80)),
            IsEnabled = contact.HasPhoneNumber
        };
        callBtn.Content = new FontIcon { Glyph = "\uE717", FontSize = 14, Foreground = new SolidColorBrush(Colors.White) };
        Grid.SetColumn(callBtn, 2);
        row.Children.Add(callBtn);

        card.Child = row;
        card.PointerEntered += (_, _) => callBtn.Visibility = Visibility.Visible;
        card.PointerExited += (_, _) => callBtn.Visibility = Visibility.Collapsed;

        return card;
    }

    private void OnCallsTab(object sender, RoutedEventArgs e)
    {
        _showingCalls = true;
        SearchBox.PlaceholderText = "Search calls...";
        UpdateTabSelection();
        RenderContacts();
    }

    private void OnContactsTab(object sender, RoutedEventArgs e)
    {
        _showingCalls = false;
        SearchBox.PlaceholderText = "Search contacts...";
        UpdateTabSelection();
        RenderContacts();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        RenderContacts();
    }
}
