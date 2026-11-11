using System.Diagnostics;
using Coworker.Models;
using Coworker.Services;
using Coworker.Services.VoiceCommands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Coworker.Controls;

/// Chooses the program an "Open App" step starts (Mac: MacAppPicker): a searchable list of the Start
/// menu apps, or "Browse…" for any .exe or shortcut.
public sealed partial class AppPicker : UserControl
{
    private IReadOnlyList<InstalledApp>? _apps;
    private string _bundleIdentifier = "";
    private string _name = "";
    private string _path = "";
    private int _iconRequest;

    /// Raised with (bundle identifier, name, path) when the user picks an app.
    public event Action<string, string, string>? Selected;

    public AppPicker()
    {
        InitializeComponent();
        RefreshTrigger();
    }

    /// Shows the app a step is configured with.
    public void SetSelection(string bundleIdentifier, string name, string path)
    {
        _bundleIdentifier = bundleIdentifier;
        _name = name;
        _path = path;
        RefreshTrigger();
    }

    private void RefreshTrigger()
    {
        var hasName = _name.Length > 0;
        NameText.Visibility = hasName ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderText.Visibility = hasName ? Visibility.Collapsed : Visibility.Visible;
        NameText.Text = _name;
        _ = ShowTriggerIconAsync();
    }

    // The saved app's own icon, when the shell can find it.
    private async Task ShowTriggerIconAsync()
    {
        var request = ++_iconRequest;
        TriggerImage.Visibility = Visibility.Collapsed;
        TriggerIcon.Visibility = Visibility.Visible;
        if (_bundleIdentifier.Length == 0 && _path.Length == 0) return;

        // A Start menu id first, then the identifier and path as plain file paths.
        var candidates = new List<string>();
        if (_bundleIdentifier.Length > 0)
        {
            candidates.Add(InstalledApp.ShellPathFor(_bundleIdentifier));
            candidates.Add(_bundleIdentifier);
        }
        if (_path.Length > 0) candidates.Add(_path);

        foreach (var candidate in candidates)
        {
            var icon = await ShellApps.LoadIconAsync(candidate, 36);
            if (request != _iconRequest) return;
            if (icon == null) continue;

            TriggerImage.Source = icon.ToImageSource();
            TriggerImage.Visibility = Visibility.Visible;
            TriggerIcon.Visibility = Visibility.Collapsed;
            return;
        }
    }

    private async void OnFlyoutOpened(object? sender, object e)
    {
        TriggerButton.BorderBrush = (Brush)Resources["CommandAccentStrokeOpen"];
        SearchBox.Focus(FocusState.Programmatic);
        if (_apps != null)
        {
            RefreshList();
            return;
        }

        SetLoading(true);
        try
        {
            _apps = await ShellApps.GetInstalledAppsAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or IOException)
        {
            _apps = Array.Empty<InstalledApp>();
        }
        SetLoading(false);
        RefreshList();
    }

    private void OnFlyoutClosed(object? sender, object e)
    {
        TriggerButton.BorderBrush = (Brush)Resources["CommandAccentStroke"];
        SearchBox.Text = "";
    }

    private void SetLoading(bool loading)
    {
        BrowseSpinner.IsActive = loading;
        BrowseSpinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = EmptyPanel.Visibility = Visibility.Collapsed;
    }

    // Filter: name or identifier contains the query, ignoring case.
    private void RefreshList()
    {
        if (_apps == null) return;

        var query = SearchBox.Text.Trim();
        var apps = (query.Length == 0
            ? _apps
            : _apps.Where(app => app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 app.Id.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList());

        var hasApps = apps.Count > 0;
        ListScroll.Visibility = hasApps ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = hasApps ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = SearchBox.Text.Length == 0 ? "No apps found" : $"No apps match \"{SearchBox.Text}\"";
        AppsRepeater.ItemsSource = apps
            .Select((app, i) => new AppOptionViewModel(app, app.Id.Equals(_bundleIdentifier, StringComparison.OrdinalIgnoreCase), i == 0))
            .ToList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ClearButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => SearchBox.Text = "";

    private void OnAppClick(object sender, RoutedEventArgs e)
    {
        var app = ((AppOptionViewModel)((FrameworkElement)sender).Tag).App;
        Select(app.Id, app.Name, app.Path);
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder, CommitButtonText = "Choose" };
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".lnk");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowService.Shared.Hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        // Like the Mac's "Browse…", the file path doubles as the identifier.
        Select(file.Path, ProgramName(file.Path), file.Path);
    }

    private void Select(string bundleIdentifier, string name, string path)
    {
        SetSelection(bundleIdentifier, name, path);
        PickerFlyout.Hide();
        Selected?.Invoke(bundleIdentifier, name, path);
    }

    // The program's own description ("Notepad") when it has one, otherwise the file name.
    private static string ProgramName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return fileName;

        var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        return string.IsNullOrWhiteSpace(description) ? fileName : description.Trim();
    }
}
