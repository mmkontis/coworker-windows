using CommunityToolkit.Mvvm.Messaging;
using Coworker.Helpers;
using Coworker.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Coworker.Services;

/// Switches the main content frame between the pages listed in <see cref="AppTabs"/>.
public sealed class NavigationService
{
    public static NavigationService Shared { get; } = new();

    private Frame? _frame;

    public AppTab CurrentTab { get; private set; } = AppTab.Home;

    /// Raised after the frame navigated (UI thread).
    public event Action<AppTab>? Navigated;

    private NavigationService()
    {
        // Mac: the OpenSettingsTab notification shows the window and switches to Settings.
        WeakReferenceMessenger.Default.Register<OpenSettingsMessage>(this, (_, _) => UiThread.Post(() =>
        {
            WindowService.Shared.Show();
            Navigate(AppTab.Settings);
        }));
    }

    public void Attach(Frame frame) => _frame = frame;

    /// Opens a tab's page. Choosing the tab that is already open keeps the page as it is (Mac: the
    /// tab switch does not fire), unless <paramref name="force"/> asks for a fresh page.
    public void Navigate(AppTab tab, bool force = false)
    {
        if (_frame == null) return;

        var pageType = AppTabs.Get(tab).PageType;
        if (!force && _frame.Content?.GetType() == pageType) return;

        CurrentTab = tab;
        _frame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
        Navigated?.Invoke(tab);
    }
}
