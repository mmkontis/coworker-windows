using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.UI;

namespace Coworker.Controls;

/// The tray panel (see TrayPanel.xaml): quick controls for dictation without opening the app. It
/// edits the same AppSettings as the Settings page.
public sealed partial class TrayPanel : UserControl
{
    private bool _isLoading;

    public TrayPanel()
    {
        InitializeComponent();
        VersionText.Text = $"v{ApiConstants.AppVersion}";

        Loaded += (_, _) =>
        {
            AppSettings.Changed += OnSettingsChanged;
            DictationController.Shared.StateChanged += OnRecordingStateChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            DictationController.Shared.StateChanged -= OnRecordingStateChanged;
        };
    }

    /// Asks the host to close the panel.
    public event Action? CloseRequested;

    private void OnSettingsChanged() => UiThread.Post(Refresh);

    private void OnRecordingStateChanged(DictationState state) => UiThread.Post(UpdateStatus);

    /// Re-reads the session and settings; called each time the panel opens.
    public void Refresh()
    {
        var signedIn = SessionService.Shared.IsAuthenticated;
        SignedInView.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedOutView.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;

        var settings = AppSettings.Instance;
        LanguageValue.Text = TrayOptions.LanguageName(settings.SelectedLanguage);
        TranslateToValue.Text = TrayOptions.TranslationName(settings.SelectedTranslationLanguage);
        TranslateToRow.Visibility = settings.TranslationEnabled ? Visibility.Visible : Visibility.Collapsed;
        _isLoading = true;
        TranslationToggle.IsOn = settings.TranslationEnabled;
        _isLoading = false;
        ShowModeButton(ChatModeButton, "Chat", settings.ChatModeEnabled);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var recording = DictationController.Shared.State == DictationState.Recording;
        StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(recording ? Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30) : Color.FromArgb(0xFF, 0x34, 0xC7, 0x59));
        StatusText.Text = recording ? "Recording" : "Ready";
    }

    // MARK: - Language and translation

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        var flyout = OptionPickerFlyout.Create(TrayOptions.DisplayLanguages, AppSettings.Instance.SelectedLanguage, code =>
        {
            AppSettings.Instance.SelectedLanguage = code;
            AppSettings.Instance.Save();
        });
        flyout.ShowAt(LanguageRow);
    }

    private void OnTranslationToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        AppSettings.Instance.TranslationEnabled = TranslationToggle.IsOn;
        AppSettings.Instance.Save();
    }

    private void OnTranslateToClick(object sender, RoutedEventArgs e)
    {
        var flyout = OptionPickerFlyout.Create(TrayOptions.TranslationLanguages, AppSettings.Instance.SelectedTranslationLanguage, code =>
        {
            AppSettings.Instance.SelectedTranslationLanguage = code;
            AppSettings.Instance.Save();
        });
        flyout.ShowAt(TranslateToRow);
    }

    // MARK: - Quick add

    // Mac modeButton: on reads in the primary colour on a faint fill, off is secondary with no fill.
    private static void ShowModeButton(Button button, string label, bool on)
    {
        var foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[on ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        var fill = on ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                      : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Foreground = foreground;
        button.Background = fill;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonBackground"] = fill;
        ToolTipService.SetToolTip(button, $"Turn {label.ToLowerInvariant()} mode {(on ? "off" : "on")}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, on ? "On" : "Off");
    }

    // Chat completion: when off, its shortcut does nothing (Mac: the hotkey manager ignores the chat target).
    private void OnChatModeClick(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Instance;
        settings.ChatModeEnabled = !settings.ChatModeEnabled;
        settings.Save();
        ShowModeButton(ChatModeButton, "Chat", settings.ChatModeEnabled);
    }

    private void OnQuickAddClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var form = new QuickAddForm(button == DictionaryButton ? QuickAddKind.Dictionary : QuickAddKind.Shortcut);
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Top,
            ShouldConstrainToRootBounds = false,
            Content = form,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["PopoverFlyoutPresenterStyle"],
        };
        form.Dismissed += flyout.Hide;
        flyout.ShowAt(button);
    }

    // MARK: - Footer

    private void OnOpenApp(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        WindowService.Shared.Show();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => App.Quit();
}
