using System.Collections.Specialized;
using System.Diagnostics;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Coworker.Settings;
using MicrophonePermission = Coworker.Settings.MicrophonePermission;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Coworker.Pages;

/// Settings, in the section order and copy of the Mac SettingsPageView. Every control edits a real
/// AppSettings value through <see cref="SettingsViewModel"/>.
public sealed partial class SettingsPage : Page
{
    private static readonly int[] MaxItemOptions = { 25, 50, 100, 200 };
    private static readonly Color Secondary40 = Color.FromArgb(0x66, 0x80, 0x80, 0x80);
    private static readonly Color Secondary12 = Color.FromArgb(0x1F, 0x80, 0x80, 0x80);
    private static readonly Color Accent12 = Color.FromArgb(0x1F, 0x24, 0x73, 0xF2);

    private readonly List<(AudioQualityChoice Choice, RadioButton Button, FontIcon Glyph)> _audioOptions = new();
    private IDisposable? _microphoneWatch;
    private bool _syncing;
    private string _appsSignature = "";
    private string _sitesSignature = "";

    public SettingsPage()
    {
        SettingsResources.EnsureLoaded();
        InitializeComponent();

        TranslateToCombo.ItemsSource = LanguageLists.Translation;
        MaxItemsCombo.ItemsSource = MaxItemOptions;
        BuildAudioQualityOptions();
        VersionText.Text = VersionString();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => SyncTheme();
    }

    public SettingsViewModel ViewModel { get; } = new();

    // MARK: - Lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Attach();
        ViewModel.PropertyChanged += OnViewModelChanged;
        ThemeService.Shared.ThemeChanged += OnThemeChanged;
        ClipboardService.Shared.Items.CollectionChanged += OnClipboardItemsChanged;
        _microphoneWatch = MicrophonePermission.Watch(() => UiThread.Post(RefreshMicrophone));
        if (App.MainWindow is { } window) window.Activated += OnWindowActivated;

        ToolsSection.Visibility = SettingsPageHooks.CaptureScreenContext == null ? Visibility.Collapsed : Visibility.Visible;
        RefreshMicrophone();
        SyncControls();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Detach();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ThemeService.Shared.ThemeChanged -= OnThemeChanged;
        ClipboardService.Shared.Items.CollectionChanged -= OnClipboardItemsChanged;
        _microphoneWatch?.Dispose();
        _microphoneWatch = null;
        if (App.MainWindow is { } window) window.Activated -= OnWindowActivated;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => SyncControls();

    private void OnThemeChanged(AppTheme theme) => SyncTheme();

    private void OnClipboardItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAutoEnterLists();

    // Back from Windows Settings after changing the microphone switch.
    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated) RefreshMicrophone();
    }

    /// Brings the controls that are not bound directly in line with the settings.
    private void SyncControls()
    {
        _syncing = true;
        try
        {
            SelectCode(TranslateToCombo, LanguageLists.Translation, ViewModel.SelectedTranslationLanguage);
            SelectCode(LanguageCombo, LanguageLists.Display, ViewModel.SelectedLanguage);
            MaxItemsCombo.SelectedIndex = Array.IndexOf(MaxItemOptions, ViewModel.MaxHistoryItems);
            DictationRecorder.Shortcut = ViewModel.DictationShortcut;
            ClipboardRecorder.Shortcut = ViewModel.ClipboardShortcut;
            SyncAudioQuality();
            SyncTheme();
        }
        finally
        {
            _syncing = false;
        }
        RebuildAutoEnterLists();
    }

    // MARK: - Permissions

    private void RefreshMicrophone() => MicrophoneCard.SetAccess(MicrophonePermission.Read());

    private void OnMicrophoneAction() =>
        _ = Launcher.LaunchUriAsync(new Uri(MicrophonePermission.PrivacySettingsUri));

    // MARK: - Appearance

    private void OnThemeCardChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string name }) return;
        ThemeService.Shared.Apply(Enum.Parse<AppTheme>(name));
    }

    private void SyncTheme()
    {
        var aurora = ThemeService.Shared.Current == AppTheme.Aurora;
        CleanThemeCard.IsChecked = !aurora;
        AuroraThemeCard.IsChecked = aurora;

        var accent = (Brush)Application.Current.Resources["CwAccentBrush"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        // The selected card's icon, tile and check take the accent; the other stays neutral.
        var cleanIcon = aurora ? secondary : accent;
        CleanRing.Stroke = cleanIcon;
        CleanHalf.Fill = cleanIcon;
        StyleThemeCard(!aurora, CleanTile, CleanCheck, accent);

        var auroraIcon = aurora ? accent : secondary;
        AuroraStarLarge.Fill = auroraIcon;
        AuroraStarTop.Fill = auroraIcon;
        AuroraStarBottom.Fill = auroraIcon;
        StyleThemeCard(aurora, AuroraTile, AuroraCheck, accent);
    }

    private static void StyleThemeCard(bool selected, Border tile, FontIcon check, Brush accent)
    {
        tile.Background = new SolidColorBrush(selected ? Accent12 : Secondary12);
        check.Glyph = selected ? "\uEC61" : "\uECCA";
        check.Foreground = selected ? accent : new SolidColorBrush(Secondary40);
    }

    // MARK: - General

    private void OnTranslateToChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && TranslateToCombo.SelectedValue is string code) ViewModel.SelectedTranslationLanguage = code;
    }

    // MARK: - Voice Shortcuts

    private void OnDictationShortcutRecorded(string shortcut) => ViewModel.DictationShortcut = shortcut;

    private void OnUseDefaultDictationShortcut(object sender, RoutedEventArgs e) => ViewModel.ResetDictationShortcut();

    // MARK: - Clipboard History

    private void OnMaxItemsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && MaxItemsCombo.SelectedItem is int value) ViewModel.MaxHistoryItems = value;
    }

    private void OnClipboardShortcutRecorded(string shortcut) => ViewModel.ClipboardShortcut = shortcut;

    private void OnOpenClipboardPanel(object sender, RoutedEventArgs e) => SettingsPageHooks.OpenClipboardPanel();

    private void OnOpenDictionary(object sender, RoutedEventArgs e) => NavigationService.Shared.Navigate(AppTab.Dictionary);

    private void RebuildAutoEnterLists()
    {
        if (!ViewModel.AutoSubmitEnabled) return;

        var options = AutoEnterApps.Options(ClipboardService.Shared.Items, AppSettings.Instance.AutoSubmitApps);
        AutoEnterEmpty.Visibility = options.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The rows keep their own switch state, so only a change in the apps themselves rebuilds them.
        var appsSignature = string.Join("|", options.Select(o => $"{o.Key}:{o.UsageCount}"));
        if (appsSignature != _appsSignature)
        {
            _appsSignature = appsSignature;
            AutoEnterAppsPanel.Children.Clear();
            foreach (var option in options)
            {
                var row = new AutoEnterAppRow(option);
                row.EnabledChanged += (name, enabled) => ViewModel.SetAutoSubmitApp(name, enabled);
                AutoEnterAppsPanel.Children.Add(row);
            }
        }

        var sites = ViewModel.AutoSubmitSites;
        var sitesSignature = string.Join("|", sites);
        AutoEnterSitesBlock.Visibility = sites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (sitesSignature != _sitesSignature)
        {
            _sitesSignature = sitesSignature;
            AutoEnterSitesPanel.Children.Clear();
            foreach (var site in sites) AutoEnterSitesPanel.Children.Add(CreateSiteRow(site));
        }
    }

    private UIElement CreateSiteRow(string site)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = site, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        texts.Children.Add(new TextBlock { Text = "Any browser tab on this site", FontSize = 11, Foreground = secondary });

        var remove = new Button
        {
            Content = "Remove",
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(remove, $"Remove {site}");
        remove.Click += (_, _) => ViewModel.RemoveAutoSubmitSite(site);

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var globe = new FontIcon { Glyph = "\uE774", FontSize = 14, Width = 22, Foreground = secondary };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(globe);
        grid.Children.Add(texts);
        grid.Children.Add(remove);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Background = (Brush)Application.Current.Resources["SettingsInsetFill"],
            Child = grid,
        };
    }

    // MARK: - Model & Language

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && LanguageCombo.SelectedValue is string code) ViewModel.SelectedLanguage = code;
    }

    /// Selects a language code in a picker, adding it first when an older version saved a code the
    /// list does not have, so the picker is never blank.
    private static void SelectCode(ComboBox combo, IReadOnlyList<LanguageItem> languages, string code)
    {
        var items = languages.ToList();
        if (items.All(i => i.Code != code)) items.Add(new LanguageItem(code, code.ToUpperInvariant()));

        if (combo.ItemsSource is not IReadOnlyList<LanguageItem> current || current.Count != items.Count)
            combo.ItemsSource = items;
        combo.SelectedValue = code;
    }

    // MARK: - Audio Quality

    private void BuildAudioQualityOptions()
    {
        foreach (var choice in AudioQualityChoice.All)
        {
            var glyph = new FontIcon { FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new TextBlock { Text = choice.DisplayName, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (choice.IsRecommended)
            {
                title.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)Application.Current.Resources["SettingsAccentFill"],
                    Child = new TextBlock
                    {
                        Text = "Recommended",
                        FontSize = 10,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                        Foreground = (Brush)Application.Current.Resources["CwAccentBrush"],
                    },
                });
            }

            var texts = new StackPanel { Spacing = 4 };
            texts.Children.Add(title);
            texts.Children.Add(new TextBlock
            {
                Text = choice.Subtitle,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            var size = new TextBlock
            {
                Text = choice.SizeEstimate,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Top,
            };

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(size, 2);
            grid.Children.Add(glyph);
            grid.Children.Add(texts);
            grid.Children.Add(size);

            var button = new RadioButton
            {
                GroupName = "AudioQuality",
                Style = (Style)Application.Current.Resources["SettingsAudioOptionStyle"],
                Content = grid,
            };
            AutomationProperties.SetName(button, $"{choice.DisplayName}, {choice.SizeEstimate}");
            var id = choice.Id;
            button.Checked += (_, _) =>
            {
                if (!_syncing) ViewModel.SelectAudioQualityPreset(id);
            };
            _audioOptions.Add((choice, button, glyph));
            AudioQualityPanel.Children.Add(button);
        }
    }

    private void SyncAudioQuality()
    {
        // Like the Mac, an unknown saved value counts as the default preset.
        var current = AudioQualityChoice.All.Any(c => c.Id == ViewModel.AudioQualityPreset)
            ? ViewModel.AudioQualityPreset
            : AudioQualityChoice.All[0].Id;

        foreach (var (choice, button, glyph) in _audioOptions)
        {
            var selected = choice.Id == current;
            button.IsChecked = selected;
            glyph.Glyph = selected ? "\uECCB" : "\uECCA";
            glyph.Foreground = (Brush)Application.Current.Resources[selected ? "CwAccentBrush" : "TextFillColorSecondaryBrush"];
        }
    }

    // MARK: - Tools and App Actions

    private void OnCapture(object sender, RoutedEventArgs e) => SettingsPageHooks.CaptureScreenContext?.Invoke();

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            if (!await UpdateService.Shared.CheckForUpdateAsync())
                ToastService.Show(ToastKind.Info, "You're up to date!", $"Coworker {ApiConstants.AppVersion} is currently the newest version available.");
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (!AppRestart.Restart())
            ToastService.Show(ToastKind.Error, null, "Couldn't restart Coworker. Quit it and open it again.");
    }

    private void OnResetOnboarding(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.HasCompletedOnboarding = false;
        AppSettings.Instance.Save();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => App.Quit();

    // MARK: - Diagnostics and About

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.Combine(AppPaths.DataDirectory, "logs");
        System.IO.Directory.CreateDirectory(folder);
        _ = Launcher.LaunchFolderPathAsync(folder);
    }

    /// "<version> (<build>)" like the Mac's "1.2 (6)": the product version and the last part of the file version.
    private static string VersionString()
    {
        var build = Environment.ProcessPath is { } path ? FileVersionInfo.GetVersionInfo(path).FilePrivatePart : 0;
        return $"{ApiConstants.AppVersion} ({build})";
    }
}
