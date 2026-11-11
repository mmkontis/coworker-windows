using System.Numerics;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Coworker.Controls;

/// The sidebar (Mac SidebarView): one of three navigation designs over a Clean or Aurora surface,
/// plus the account footer and its popover. What it shows follows NavigationService, SessionService,
/// ThemeService and the persisted design.
public sealed partial class Sidebar : UserControl
{
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan MoreDuration = TimeSpan.FromMilliseconds(150);

    private readonly Dictionary<AppTab, SidebarRow> _rows = new();
    private readonly Flyout _workspaceFlyout;
    private readonly StackPanel _workspaceOptions;
    private readonly Flyout _profileFlyout;
    private readonly ProfileMenu _profileMenu = new();
    private WorkspaceDesign _design;
    private WorkspaceArea _activeArea = WorkspaceArea.Transcribe;
    private WorkspaceArea? _expandedArea = WorkspaceArea.Transcribe;
    private bool _showMore;

    public Sidebar()
    {
        InitializeComponent();

        _workspaceOptions = new StackPanel { Spacing = 3 };
        _workspaceFlyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
            Content = _workspaceOptions,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["WorkspaceFlyoutPresenterStyle"],
        };
        _workspaceFlyout.Opened += (_, _) => FocusOption(_activeArea);

        _profileFlyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
            Content = _profileMenu,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["PopoverFlyoutPresenterStyle"],
        };
        _profileFlyout.Opened += (_, _) => UserChevron.Rotation = 180;
        _profileFlyout.Closed += (_, _) => UserChevron.Rotation = 0;
        _profileFlyout.Opened += (_, _) => UserButton.Background = (Brush)Application.Current.Resources["SidebarSelectedFill"];
        _profileFlyout.Closed += (_, _) => UserButton.ClearValue(Control.BackgroundProperty);
        _profileMenu.Dismissed += () => _profileFlyout.Hide();

        UserChevron.RotationTransition = new ScalarTransition { Duration = MoreDuration };
        UserChevron.SizeChanged += (_, _) => UserChevron.CenterPoint = new Vector3((float)UserChevron.ActualWidth / 2, (float)UserChevron.ActualHeight / 2, 0);

        ElementShadow.Attach(LogoShadow, 9, Color.FromArgb(0x40, 0, 0, 0), 6, 1);
        ElementShadow.Attach(AvatarShadow, 16, Color.FromArgb(0x4D, 0x4D, 0xD9, 0xFF), 8, 2);

        NavigationService.Shared.Navigated += OnNavigated;
        ThemeService.Shared.ThemeChanged += _ => ApplyTheme();
        SessionService.Shared.SessionChanged += _ => UpdateAccount();
        AppSettings.Changed += OnSettingsChanged;
        ActualThemeChanged += (_, _) => ApplyTheme();

        _design = SidebarModel.Design;
        SyncArea(NavigationService.Shared.CurrentTab);
        ApplyTheme();
        UpdateAccount();
        Rebuild();
    }

    // MARK: - Theme

    private void ApplyTheme()
    {
        var aurora = ThemeService.Shared.Current == AppTheme.Aurora;
        AuroraArtwork.Visibility = aurora ? Visibility.Visible : Visibility.Collapsed;
        AuroraFade.Visibility = aurora ? Visibility.Visible : Visibility.Collapsed;
        CleanBorder.Visibility = aurora ? Visibility.Collapsed : Visibility.Visible;

        // The fade blends the artwork into the window background: clear, then that colour at 15% and 40%.
        var background = ((SolidColorBrush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]).Color;
        FadeMid.Color = Color.FromArgb(0x26, background.R, background.G, background.B);
        FadeEnd.Color = Color.FromArgb(0x66, background.R, background.G, background.B);

        PlanText.Foreground = aurora
            ? new SolidColorBrush(Color.FromArgb(0xD9, 0x4D, 0xD9, 0xFF))
            : (Brush)Application.Current.Resources["CwAccentBrush"];
    }

    // MARK: - State

    private void OnNavigated(AppTab tab)
    {
        _profileFlyout.Hide();
        _workspaceFlyout.Hide();
        var before = _activeArea;
        SyncArea(tab);
        if (_activeArea != before) Rebuild();
        else UpdateSelection();
    }

    private void OnSettingsChanged()
    {
        if (SidebarModel.Design == _design) return;
        _design = SidebarModel.Design;
        _workspaceFlyout.Hide();
        Rebuild();
    }

    /// Mac syncArea(for:): the picker follows the tab; Settings leaves it where it was.
    private void SyncArea(AppTab tab)
    {
        if (SidebarModel.AreaFor(tab) is not { } area) return;
        _activeArea = area;
        _expandedArea = area;
    }

    private void SelectArea(WorkspaceArea area)
    {
        _activeArea = area;
        _expandedArea = area;
        NavigationService.Shared.Navigate(area.Tab());
    }

    private string? Email => SessionService.Shared.UserEmail;

    // MARK: - Navigation

    private void Rebuild()
    {
        _rows.Clear();
        NavHost.Children.Clear();

        WorkspaceButton.Visibility = _design == WorkspaceDesign.Dropdown ? Visibility.Visible : Visibility.Collapsed;
        // The shortcut card sits above the account row in every design.
        HotkeyHost.Visibility = Visibility.Visible;
        HotkeyHost.Child ??= new SidebarHotkeyCard();

        switch (_design)
        {
            case WorkspaceDesign.Classic:
                var flat = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 0) };
                foreach (var item in SidebarModel.ClassicItems()) flat.Children.Add(NewRow(item));
                NavHost.Children.Add(flat);
                break;

            case WorkspaceDesign.Dropdown:
                UpdatePicker();
                NavHost.Children.Add(BuildArea(_activeArea));
                break;

            default:
                foreach (var area in Enum.GetValues<WorkspaceArea>())
                {
                    NavHost.Children.Add(BuildSectionHeader(area));
                    if (_expandedArea == area) NavHost.Children.Add(BuildArea(area));
                }
                break;
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var highlighted = AppTabs.HighlightFor(NavigationService.Shared.CurrentTab);
        foreach (var (tab, row) in _rows) row.IsSelected = tab == highlighted;
    }

    private SidebarRow NewRow(SidebarItem item)
    {
        var row = new SidebarRow(item);
        row.Invoked += tab => NavigationService.Shared.Navigate(tab);
        _rows[item.Tab] = row;
        return row;
    }

    private StackPanel BuildArea(WorkspaceArea area)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 12, 0) };
        foreach (var item in SidebarModel.AreaItems(area)) panel.Children.Add(NewRow(item));
        if (area == WorkspaceArea.Transcribe) AddMore(panel);
        return panel;
    }

    /// The "More" expander under the Transcribe rows.
    private void AddMore(StackPanel panel)
    {
        var chevron = new FontIcon
        {
            Glyph = "",
            FontSize = 9,
            Foreground = (Brush)Application.Current.Resources["SidebarSecondaryText"],
            Rotation = _showMore ? 180 : 0,
            RotationTransition = new ScalarTransition { Duration = MoreDuration },
        };
        chevron.Loaded += (_, _) => chevron.CenterPoint = new Vector3((float)chevron.ActualWidth / 2, (float)chevron.ActualHeight / 2, 0);

        var secondary = (Brush)Application.Current.Resources["SidebarSecondaryText"];
        var content = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ellipsis = new FontIcon { Glyph = "", FontSize = 13, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = "More", FontSize = 13, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(ellipsis);
        content.Children.Add(label);
        content.Children.Add(chevron);

        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["SidebarPlainButtonStyle"],
        };
        AutomationProperties.SetName(button, "More");
        AutomationProperties.SetAutomationId(button, "nav-more");
        panel.Children.Add(button);

        var more = new StackPanel { Spacing = 2, Visibility = _showMore ? Visibility.Visible : Visibility.Collapsed };
        foreach (var item in SidebarModel.MoreItems(Email)) more.Children.Add(NewRow(item));
        panel.Children.Add(more);

        button.Click += (_, _) =>
        {
            _showMore = !_showMore;
            chevron.Rotation = _showMore ? 180 : 0;
            more.Visibility = _showMore ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, _showMore ? "Expanded" : "Collapsed");
        };
    }

    /// Sections design: an area's header, which expands or collapses its rows (Mac workspaceNavigation).
    private Button BuildSectionHeader(WorkspaceArea area)
    {
        var expanded = _expandedArea == area;
        var brush = (Brush)Application.Current.Resources[_activeArea == area ? "SidebarPrimaryText" : "SidebarSecondaryText"];

        var chevron = new FontIcon
        {
            Glyph = "",
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Rotation = expanded ? 90 : 0,
            RotationTransition = new ScalarTransition { Duration = ExpandDuration },
        };
        chevron.Loaded += (_, _) => chevron.CenterPoint = new Vector3((float)chevron.ActualWidth / 2, (float)chevron.ActualHeight / 2, 0);

        var content = new Grid { Padding = new Thickness(12, 11, 12, 11), ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Child = SidebarIcons.For(area), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = area.Title(), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(icon);
        content.Children.Add(label);
        content.Children.Add(chevron);

        var button = new Button
        {
            Content = content,
            Foreground = brush,
            Margin = new Thickness(12, 0, 12, 0),
            Style = (Style)Application.Current.Resources["SidebarPlainButtonStyle"],
        };
        AutomationProperties.SetName(button, area.Title());
        AutomationProperties.SetAutomationId(button, $"workspace-section-{area}");
        AutomationProperties.SetItemStatus(button, expanded ? "Expanded" : "Collapsed");
        button.Click += (_, _) =>
        {
            if (_expandedArea == area)
            {
                _expandedArea = null;
                Rebuild();
            }
            else
            {
                SelectArea(area);   // navigates; the tab change rebuilds when the area changes
                Rebuild();
            }
        };
        return button;
    }

    // MARK: - Workspace picker

    private void UpdatePicker()
    {
        WorkspaceIcon.Child = SidebarIcons.For(_activeArea);
        WorkspaceName.Text = _activeArea.Title();
        AutomationProperties.SetItemStatus(WorkspaceButton, _activeArea.Title());
    }

    private void OnWorkspaceClick(object sender, RoutedEventArgs e)
    {
        BuildWorkspaceOptions();
        _workspaceFlyout.ShowAt(WorkspaceButton);
    }

    private void BuildWorkspaceOptions()
    {
        // Border 1 + padding 6 on each side.
        _workspaceOptions.Width = Math.Max(0, WorkspaceButton.ActualWidth - 14);
        _workspaceOptions.Children.Clear();

        foreach (var area in Enum.GetValues<WorkspaceArea>())
        {
            var active = area == _activeArea;
            var check = new FontIcon { Glyph = "", FontSize = 11, Visibility = active ? Visibility.Visible : Visibility.Collapsed };
            var content = new Grid { Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 10, Height = 38 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Child = SidebarIcons.For(area), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = area.Title(), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(check, 2);
            content.Children.Add(icon);
            content.Children.Add(label);
            content.Children.Add(check);

            var button = new Button
            {
                Content = content,
                Tag = area,
                CornerRadius = new CornerRadius(4),
                Style = (Style)Application.Current.Resources["PopoverRowButtonStyle"],
            };
            if (active) button.Background = (Brush)Application.Current.Resources["PopoverHoverFill"];
            AutomationProperties.SetName(button, area.Title());
            AutomationProperties.SetAutomationId(button, $"workspace-option-{area}");
            AutomationProperties.SetItemStatus(button, active ? "Selected" : "");
            button.Click += (_, _) =>
            {
                _workspaceFlyout.Hide();
                SelectArea(area);
            };
            button.KeyDown += OnWorkspaceOptionKeyDown;
            _workspaceOptions.Children.Add(button);
        }
    }

    private void FocusOption(WorkspaceArea area)
    {
        var option = _workspaceOptions.Children.OfType<Button>().FirstOrDefault(b => (WorkspaceArea)b.Tag == area);
        option?.Focus(FocusState.Keyboard);
    }

    /// Mac: Up and Down cycle through the areas (Return activates the focused one, Escape closes).
    private void OnWorkspaceOptionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;

        var options = _workspaceOptions.Children.OfType<Button>().ToList();
        var index = options.IndexOf((Button)sender);
        var step = e.Key == VirtualKey.Down ? 1 : -1;
        options[(index + step + options.Count) % options.Count].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    // MARK: - Account footer

    private void UpdateAccount()
    {
        var email = Email;
        if (string.IsNullOrEmpty(email)) return;   // signed out: the window shows the sign-in screen instead

        AvatarInitial.Text = email[..1].ToUpperInvariant();
        UserName.Text = AppSettings.Instance.UserFullName is { Length: > 0 } name ? name : SidebarModel.DisplayName(email);
        ShowAvatarPhoto(AppSettings.Instance.UserPhotoUrl);
        // The Mac lists the internal tabs behind "More" only for one account.
        Rebuild();
    }

    private string? _avatarPhotoUrl;

    // Loads the account photo into the avatar circle; the initial stays underneath until it arrives,
    // and stays for good when there is no photo or it can't be fetched.
    private void ShowAvatarPhoto(string? url)
    {
        if (url == _avatarPhotoUrl) return;
        _avatarPhotoUrl = url;
        AvatarPhoto.Visibility = Visibility.Collapsed;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            AvatarPhotoBrush.ImageSource = null;
            return;
        }

        // Decoded at the circle's pixel size on this display: sharp, and no full-size photo kept in memory.
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        AvatarPhotoBrush.ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri)
        {
            DecodePixelWidth = (int)Math.Ceiling(32 * Math.Max(scale, 2)),
            DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical,
        };
    }

    private void OnAvatarPhotoOpened(object sender, RoutedEventArgs e) => AvatarPhoto.Visibility = Visibility.Visible;

    private void OnAvatarPhotoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        DebugLog.Write($"Avatar photo could not be loaded ({e.ErrorMessage})");
        AvatarPhoto.Visibility = Visibility.Collapsed;
    }

    private void OnUserClick(object sender, RoutedEventArgs e)
    {
        _workspaceFlyout.Hide();
        if (Email is not { Length: > 0 } email) return;

        _profileMenu.Refresh(email);
        _profileFlyout.ShowAt(UserButton);
    }
}
