using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Microsoft.UI.Dispatching;

namespace Coworker.Paste;

/// <summary>
/// Offers Auto-Enter for an app or site after the user has pressed Return by hand right after three
/// confirmed dictation pastes there (Mac: AutoSendSuggestionController). It watches only the two seconds
/// after a paste, and only for timing: a plain Return in the same app counts as a send; any other key,
/// click or app switch ends the watch. Nothing about what was typed is kept. UI thread only.
/// </summary>
internal sealed class AutoSendSuggestionController
{
    public static AutoSendSuggestionController Shared { get; } = new();

    private static readonly TimeSpan ObserveFor = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FocusCheckEvery = TimeSpan.FromMilliseconds(100);
    private const uint VkReturn = 0x0D;

    private readonly AutoSendSuggestionTracker _tracker = new();
    private readonly HashSet<string> _offered = new();   // once per session and destination
    private DispatcherQueueTimer? _expiry;
    private DispatcherQueueTimer? _focusCheck;
    private string _watchedKey = "";
    private IntPtr _watchedWindow;
    private string _watchedApp = "";
    private string? _watchedSite;
    private int _generation;

    private AutoSendSuggestionController() { }

    /// A paste the user did not have Auto-Enter for was confirmed in <paramref name="target"/>.
    public void RecordPaste(FocusTarget target)
    {
        StopObserving();
        _ = RecordPasteAsync(target);
    }

    private async Task RecordPasteAsync(FocusTarget target)
    {
        var generation = ++_generation;
        var app = target.App;

        // In a browser the question is about the tab's site, so each site gets its own offer.
        var site = BrowserSite.IsBrowser(app.ProcessName) ? await BrowserSite.HostOfWindowAsync(target.Window) : null;
        var key = site != null ? $"{app.ExePath}|{site}" : app.ExePath;
        if (generation != _generation || key.Length == 0 || _offered.Contains(key) ||
            AutoEnterRules.ShouldAutoSubmit(app.DisplayName, site) || ForegroundWindowNow() != target.Window) return;

        StopObserving();
        _watchedKey = key;
        _watchedWindow = target.Window;
        _watchedApp = app.DisplayName;
        _watchedSite = site;
        _tracker.Pasted(key, Environment.TickCount64);

        GlobalHotkeyService.Instance?.SetObserver(OnInput);
        _expiry = StartTimer(ObserveFor, StopObserving);
        _focusCheck = StartTimer(FocusCheckEvery, () =>
        {
            if (ForegroundWindowNow() != _watchedWindow) StopObserving();
        }, repeating: true);
    }

    private void OnInput(InputObservation input)
    {
        if (_watchedKey.Length == 0) return;

        var isReturn = !input.IsMouse && input.Vk == VkReturn && input.Modifiers == ShortcutModifiers.None;
        if (!isReturn || ForegroundWindowNow() != _watchedWindow)
        {
            // Edits, newline shortcuts, clicks and app switches are not send intent.
            StopObserving();
            return;
        }

        var key = _watchedKey;
        var offer = _tracker.PressedEnter(key, input.Timestamp);
        var app = _watchedApp;
        var site = _watchedSite;
        StopObserving();
        if (offer && !AutoEnterRules.ShouldAutoSubmit(app, site)) ShowOffer(key, app, site);
    }

    private void StopObserving()
    {
        _generation++;
        _watchedKey = "";
        GlobalHotkeyService.Instance?.SetObserver(null);
        _expiry?.Stop();
        _expiry = null;
        _focusCheck?.Stop();
        _focusCheck = null;
        _tracker.CancelPending();
    }

    private void ShowOffer(string key, string appName, string? site)
    {
        _offered.Add(key);
        var destination = site ?? appName;

        ToastService.Show(ToastKind.Info, null, $"Auto Enter in {destination}?", null, "Turn on", () =>
        {
            var settings = AppSettings.Instance;
            var wasEnabled = settings.AutoSubmitEnabled;
            var wasSelected = site != null ? AutoEnterRules.IsSiteSelected(site) : AutoEnterRules.IsAppSelected(appName);

            if (site != null) AutoEnterRules.SetSite(site, true);
            else AutoEnterRules.SetApp(appName, true);
            settings.AutoSubmitEnabled = true;
            settings.Save();

            var enabledApps = settings.AutoSubmitApps.ToList();
            var enabledSites = settings.AutoSubmitSites.ToList();
            ToastService.Show(ToastKind.Info, null, $"Auto Enter on for {destination}", 5, "Undo", () =>
            {
                var unchanged = settings.AutoSubmitApps.SequenceEqual(enabledApps) && settings.AutoSubmitSites.SequenceEqual(enabledSites);
                if (site != null) AutoEnterRules.SetSite(site, wasSelected);
                else AutoEnterRules.SetApp(appName, wasSelected);
                if (unchanged) settings.AutoSubmitEnabled = wasEnabled;
                settings.Save();
                ToastService.Show(ToastKind.Info, null, "Preference restored", 5);
            });
        });
    }

    private static IntPtr ForegroundWindowNow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == IntPtr.Zero ? IntPtr.Zero : ProcessNative.GetAncestor(foreground, ProcessNative.GA_ROOT);
    }

    private static DispatcherQueueTimer StartTimer(TimeSpan interval, Action tick, bool repeating = false)
    {
        var timer = UiThread.Queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return timer;
    }
}
