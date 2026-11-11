using Coworker.Models;
using Coworker.Services;

namespace Coworker.Settings;

/// Where other features plug into the Settings page. Assign once at startup.
public static class SettingsPageHooks
{
    /// Opens the Screen Context capture window. While null the "Tools" section stays hidden, as
    /// on the Mac where the section only appears when the host supplies the callback.
    public static Action? CaptureScreenContext { get; set; }

    /// Opens the clipboard history panel ("Open Clipboard Panel"). Until the panel exists this
    /// shows the clipboard history in the main window, which is what the clipboard shortcut does.
    public static Action OpenClipboardPanel { get; set; } = () =>
    {
        WindowService.Shared.Show();
        NavigationService.Shared.Navigate(AppTab.History);
    };
}
