using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Coworker.Controls;

/// The popover over the sidebar footer: account, Settings, Appearance & navigation, sign-out.
public sealed partial class ProfileMenu : UserControl
{
    public ProfileMenu()
    {
        InitializeComponent();

        SectionsRow.Tag = WorkspaceDesign.Sections;
        DropdownRow.Tag = WorkspaceDesign.Dropdown;
        ClassicRow.Tag = WorkspaceDesign.Classic;
        SectionsRow.Title = WorkspaceDesign.Sections.Title();
        DropdownRow.Title = WorkspaceDesign.Dropdown.Title();
        ClassicRow.Title = WorkspaceDesign.Classic.Title();
        CleanRow.Tag = AppTheme.Clean;
        AuroraRow.Tag = AppTheme.Aurora;
    }

    /// Asks the host to close the popover.
    public event Action? Dismissed;

    /// Rebuilds the parts that depend on who is signed in and on the navigation design.
    public void Refresh(string email)
    {
        EmailText.Text = email;
        SettingsRow.IsSelected = NavigationService.Shared.CurrentTab == AppTab.Settings;
        AppearanceOptions.Visibility = Visibility.Collapsed;
        UpdateSelection();

        MoreRows.Children.Clear();
        var more = SidebarModel.Design == WorkspaceDesign.Classic ? SidebarModel.ClassicMoreItems(email) : Array.Empty<SidebarItem>();
        foreach (var item in more)
        {
            var row = new ProfileMenuRow
            {
                Glyph = AppTabs.Get(item.Tab).Glyph,
                Title = item.Title,
                IsSelected = NavigationService.Shared.CurrentTab == item.Tab,
                Tag = item.Tab,
            };
            row.Click += OnMoreClick;
            MoreRows.Children.Add(row);
        }
        MoreSection.Visibility = more.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Header.Margin = new Thickness(14, 14, 14, more.Count > 0 ? 12 : 10);
    }

    private void UpdateSelection()
    {
        var design = SidebarModel.Design;
        SectionsRow.IsSelected = design == WorkspaceDesign.Sections;
        DropdownRow.IsSelected = design == WorkspaceDesign.Dropdown;
        ClassicRow.IsSelected = design == WorkspaceDesign.Classic;
        CleanRow.IsSelected = ThemeService.Shared.Current == AppTheme.Clean;
        AuroraRow.IsSelected = ThemeService.Shared.Current == AppTheme.Aurora;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        NavigationService.Shared.Navigate((AppTab)((ProfileMenuRow)sender).Tag);
        Dismissed?.Invoke();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        NavigationService.Shared.Navigate(AppTab.Settings);
        Dismissed?.Invoke();
    }

    private void OnAppearanceClick(object sender, RoutedEventArgs e) =>
        AppearanceOptions.Visibility = AppearanceOptions.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OnDesignClick(object sender, RoutedEventArgs e)
    {
        SidebarModel.Design = (WorkspaceDesign)((ProfileMenuRow)sender).Tag;
        UpdateSelection();
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        ThemeService.Shared.Apply((AppTheme)((ProfileMenuRow)sender).Tag);
        UpdateSelection();
    }

    private void OnEditProfileClick(object sender, RoutedEventArgs e) => Open(ApiConstants.CoworkerDashboardUrl);

    private void OnBillingClick(object sender, RoutedEventArgs e) => Open(ApiConstants.CoworkerBillingUrl);

    private void OnLogOutClick(object sender, RoutedEventArgs e)
    {
        _ = AccountActions.SignOutAsync();
        Dismissed?.Invoke();
    }

    private void Open(string url)
    {
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        Dismissed?.Invoke();
    }
}
