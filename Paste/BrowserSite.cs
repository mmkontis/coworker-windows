using Coworker.Helpers;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Coworker.Paste;

/// The site open in a browser tab, read from the address bar through UI Automation (Mac: BrowserSite,
/// which reads the AXWebArea URL). Best effort: a browser that hides or renames its address bar
/// gives no site, and the app-level Auto-Enter rule still applies.
public static class BrowserSite
{
    // Programs that browse the web: Chromium builds, Firefox and its forks.
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "chromium", "thorium",
        "browser" /* Yandex */, "zen", "librewolf", "waterfox", "floorp", "iexplore",
    };

    // The address bar: Chromium's omnibox and Firefox's URL bar.
    private const string ChromiumAddressBarClass = "OmniboxViewViews";
    private const string FirefoxAddressBarId = "urlbar-input";

    public static bool IsBrowser(string processName) => Browsers.Contains(processName);

    /// The normalized host ("chatgpt.com") of the tab in the given browser window, or null.
    /// Blocks on the browser: call from a background thread.
    public static string? HostOfWindow(UIA3Automation automation, IntPtr window)
    {
        try
        {
            var root = automation.FromHandle(window);
            var factory = automation.ConditionFactory;
            var bar = root.FindFirstDescendant(factory.ByClassName(ChromiumAddressBarClass).Or(factory.ByAutomationId(FirefoxAddressBarId)));
            var text = bar?.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            return AutoEnterRules.NormalizedHost(text);
        }
        catch (Exception ex) when (FocusTracker.IsUiaFailure(ex))
        {
            DebugLog.Write($"Browser address bar not read: {ex.Message}");
            return null;
        }
    }

    /// The host of the tab in the browser window <paramref name="window"/> when it belongs to a browser, else null.
    public static Task<string?> HostOfWindowAsync(IntPtr window)
    {
        var app = ForegroundApp.Describe(window);
        if (app == null || !IsBrowser(app.ProcessName)) return Task.FromResult<string?>(null);

        var worker = FocusTracker.Shared.Worker;
        return worker.Run(() => HostOfWindow(worker.Automation, app.Window));
    }
}
