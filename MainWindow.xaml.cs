using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;

namespace Coworker;

/// The shell: the sidebar and page frame while signed in, the sign-in screen when signed out, and the
/// onboarding on first run (Mac AppView). Window chrome, tray, recording and navigation live in their
/// own services.
public sealed partial class MainWindow : Window
{
    private enum ShellState
    {
        Onboarding,
        SignedOut,
        Main,
    }

    private ShellState? _state;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Coworker";

        ThemeService.Shared.Attach((FrameworkElement)Content);

        var windows = WindowService.Shared;
        windows.Initialize(this, TitleBarDragRegion);
        TrayService.Shared.Initialize();
        NavigationService.Shared.Attach(ContentFrame);
        NavigationService.Shared.Navigated += OnNavigated;

        // Mac: Settings... is Cmd+, ("Ctrl+," here). A KeyboardAccelerator cannot express the comma
        // key (WinUI fails while resolving it), so the shortcut is read from KeyDown.
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);

        // WinUI hands the first control the initial focus and draws its ring; the Mac shows none until a key is used.
        RootGrid.Loaded += (_, _) => ContentFrame.Focus(FocusState.Pointer);

        Onboarding.Completed += () =>
        {
            AppSettings.Instance.HasCompletedOnboarding = true;
            AppSettings.Instance.Save();
            UpdateShellState();
        };
        SessionService.Shared.SessionChanged += _ => UpdateShellState();
        AppSettings.Changed += () => UiThread.Post(UpdateShellState);
        UpdateShellState();

        ClipboardService.Shared.StartMonitoring();
        SetupGlobalHotkeys();
        TranscribeState.Start();
        UpdateService.Shared.StartPeriodicChecks();
        SessionService.Shared.Start();
        RealtimeService.Shared.Start();
    }

    /// Kept for pages that navigate through the window; prefer <see cref="NavigationService"/>.
    public void NavigateTo(AppTab tab) => NavigationService.Shared.Navigate(tab);

    private void SetupGlobalHotkeys()
    {
        DictationService.Shared.Start();
        // The dictation controller opens the sign-in prompt itself; show it in the shell's Upgrade window.
        UpgradePrompt.Show = request => UpgradeService.Show(request.Message, request.Url, request.CurrentUsage, request.Limit);

        DictationService.Shared.Hotkeys.ClipboardHotkeyPressed += () =>
        {
            WindowService.Shared.Show();
            NavigationService.Shared.Navigate(AppTab.History);
        };
    }

    // MARK: - Shell state

    /// Mac AppView body: onboarding first, then the sign-in screen, then the app.
    private void UpdateShellState()
    {
        var state = !AppSettings.Instance.HasCompletedOnboarding ? ShellState.Onboarding
            : !SessionService.Shared.IsAuthenticated ? ShellState.SignedOut
            : ShellState.Main;
        if (state == _state) return;

        var previous = _state;
        _state = state;
        MainLayout.Visibility = state == ShellState.Main ? Visibility.Visible : Visibility.Collapsed;
        LoginPage.Visibility = state == ShellState.SignedOut ? Visibility.Visible : Visibility.Collapsed;
        Onboarding.Visibility = state == ShellState.Onboarding ? Visibility.Visible : Visibility.Collapsed;
        if (state == ShellState.Onboarding) Onboarding.Restart();

        // The sign-in and onboarding backgrounds are dark whatever the theme.
        WindowService.Shared.SetCaptionButtonsLight(state != ShellState.Main);

        if (state != ShellState.Main) return;
        if (previous != ShellState.Main || ContentFrame.Content == null)
        {
            NavigationService.Shared.Navigate(AppTab.Home, force: true);
            _ = AppState.Shared.RefreshDictionaryAsync();
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        const Windows.System.VirtualKey comma = (Windows.System.VirtualKey)0xBC;
        if (e.Key != comma || _state != ShellState.Main) return;
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

        e.Handled = true;
        NavigationService.Shared.Navigate(AppTab.Settings);
    }

    private static void OnNavigated(AppTab tab)
    {
        // Mac: selecting Home refetches the stats.
        if (tab == AppTab.Home) _ = AppState.Shared.RefreshStatsAsync();
    }
}
