using Coworker.Dictation;
using System.Globalization;
using Coworker.Helpers;
using Coworker.Services.Updates;
using Coworker.Views;

namespace Coworker.Services;

/// <summary>
/// Auto-update for the Windows app: the counterpart of Sparkle + UpdaterDelegate on the Mac.
///
/// Feed:      https://humanlike.co/coworker/appcast-windows.xml (Sparkle format, Windows items only,
///            separate from the macOS appcast.xml so neither app can mistake the other's releases).
/// Trust:     every installer must carry a valid Ed25519 signature for the public key built into the app
///            (see <see cref="UpdateKeys"/>); no key, no updates.
/// Cadence:   Info.plist SUScheduledCheckInterval = 3600 s, a first check shortly after launch, and the
///            critical-update check 5 s after launch (UpdaterDelegate.startChecks).
/// Default:   SUAutomaticallyUpdate + willInstallUpdateOnQuit(immediate): background checks download a new
///            build silently, then install and relaunch right away - here only once no dictation is in flight.
/// Manual:    <see cref="CheckForUpdatesAsync"/>(true) from the tray and Settings opens the Sparkle-style
///            window: Checking / Up to date / Update available (release notes, Skip / Remind Me Later /
///            Install Update) / Downloading / Installing. A critical update also raises the "Update Required"
///            alert (Update Now / Quit).
/// </summary>
public sealed class UpdateService
{
    public static UpdateService Shared { get; } = new();

    /// Mac: SUScheduledCheckInterval.
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3600);

    /// Mac: UpdaterDelegate.startChecks runs the critical-update check 5 s after launch.
    private static readonly TimeSpan CriticalCheckDelay = TimeSpan.FromSeconds(5);

    /// The first scheduled check, when one is already due.
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan WaitForIdlePoll = TimeSpan.FromSeconds(5);

    /// Mac: CFBundleDisplayName ?? CFBundleName ?? "Coworker".
    private const string AppDisplayName = "Coworker";

    private const string LocalFeedUrl = "http://localhost:3000/coworker/appcast-windows.xml";

    private readonly object _sync = new();
    private Task<UpdateEngine>? _engineTask;
    private bool _started;
    private volatile bool _dictating;
    private volatile bool _backgroundBusy;
    private volatile bool _requiredAlertShown;
    private UpdateWindow? _window;

    private UpdateService() { }

    /// Mac SUAutomaticallyUpdate: download and install new builds in the background. Off = only the
    /// manual "Check for Updates…" flow installs anything.
    public bool AutomaticallyInstall { get; set; } = true;

    /// False while the build carries no update public key (<see cref="UpdateKeys"/>): nothing can be trusted.
    public bool IsConfigured => UpdateKeys.IsConfigured;

    /// When the feed was last read successfully (null before the first successful check).
    public DateTimeOffset? LastCheckTime { get; private set; }

    /// True while the "Check for Updates…" window is open.
    public bool IsUserFlowActive => _window != null;

    /// Starts the background checks. Called once from the main window.
    public void StartPeriodicChecks()
    {
        if (_started) return;
        _started = true;

        // The recording state is UI-thread only; keep a copy the updater can read from any thread.
        UiThread.Post(() =>
        {
            _dictating = DictationController.Shared.State != DictationState.Idle;
            DictationController.Shared.StateChanged += state => _dictating = state != DictationState.Idle;
        });

        if (!IsConfigured)
        {
            DebugLog.Write("Updates are off: this build has no update public key (set UpdatePublicKey in Coworker.csproj).");
            return;
        }

        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--check-for-updates", StringComparison.OrdinalIgnoreCase)))
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => CheckForUpdatesAsync(userInitiated: true), TaskScheduler.Default);

        _ = Task.Run(RunSchedulerAsync);
    }

    /// The hook for "Check for Updates…" in the tray menu and Settings > App Actions.
    /// <paramref name="userInitiated"/> true: open the update window and show every outcome, like Sparkle's
    /// checkForUpdates. False: a quiet background check (what the hourly timer runs).
    public Task CheckForUpdatesAsync(bool userInitiated) =>
        userInitiated ? OnUiThreadAsync(RunUserFlowAsync) : RunBackgroundCheckAsync();

    /// Foundation-era entry point, kept so callers written against the placeholder still compile
    /// (Settings > App Actions > Check for Updates). It runs the same window as
    /// <c>CheckForUpdatesAsync(true)</c>, which reports every outcome itself ("You're up to date!" included),
    /// so it always returns true = "handled, show nothing more". New code should call
    /// <c>CheckForUpdatesAsync(userInitiated: true)</c>.
    public async Task<bool> CheckForUpdateAsync()
    {
        await CheckForUpdatesAsync(userInitiated: true);
        return true;
    }

    // ------------------------------------------------------------------------------------------------
    // Background: critical-update alert + hourly quiet checks
    // ------------------------------------------------------------------------------------------------

    private async Task RunSchedulerAsync()
    {
        try
        {
            var engine = await GetEngineAsync().ConfigureAwait(false);
            LastCheckTime ??= engine.LastCheckTime;

            // Sparkle's scheduled check is due once the last one is older than SUScheduledCheckInterval; on a
            // fresh install (or after a long pause) it runs shortly after launch. The critical-update probe
            // below is separate and does not postpone it.
            var firstDue = engine.LastCheckTime is { } last ? last + CheckInterval - DateTimeOffset.UtcNow : TimeSpan.Zero;
            if (firstDue < FirstCheckDelay) firstDue = FirstCheckDelay;
            var launched = DateTimeOffset.UtcNow;

            await Task.Delay(CriticalCheckDelay).ConfigureAwait(false);
            await CheckForCriticalUpdateAsync().ConfigureAwait(false);

            var wait = firstDue - (DateTimeOffset.UtcNow - launched);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);

            while (true)
            {
                await RunBackgroundCheckAsync().ConfigureAwait(false);
                await Task.Delay(CheckInterval).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Update scheduler stopped: {ex.Message}");
        }
    }

    /// Mac checkForCriticalUpdate: the feed offers something newer and marks it critical -> "Update Required".
    private async Task CheckForCriticalUpdateAsync()
    {
        try
        {
            var engine = await GetEngineAsync().ConfigureAwait(false);
            var result = await engine.CheckAsync(ignoreSkippedVersions: true, recordCheckTime: false).ConfigureAwait(false);
            if (result.Status != UpdateCheckStatus.UpdateAvailable || !result.AnyCritical || result.Latest == null) return;

            await OnUiThreadAsync(() => ShowRequiredAlertAsync(result.Latest.ShortVersion)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Critical update check failed: {ex.Message}");
        }
    }

    private async Task RunBackgroundCheckAsync()
    {
        if (!IsConfigured || IsUserFlowActive || _backgroundBusy) return;
        _backgroundBusy = true;
        try
        {
            var engine = await GetEngineAsync().ConfigureAwait(false);
            var result = await engine.CheckAsync(ignoreSkippedVersions: false).ConfigureAwait(false);
            LastCheckTime = engine.LastCheckTime;

            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    engine.CleanUpDownloads();   // installers of builds we already run
                    break;

                case UpdateCheckStatus.UpdateAvailable when AutomaticallyInstall && result.Latest != null:
                    DebugLog.Write($"Update {result.Latest} found; downloading in the background.");
                    var path = await engine.DownloadAsync(result.Latest).ConfigureAwait(false);
                    await InstallWhenIdleAsync(engine, result.Latest, path).ConfigureAwait(false);
                    break;

                case UpdateCheckStatus.Failed:
                    DebugLog.Write($"Background update check failed: {result.Error}");
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Background update failed: {ex.Message}");
        }
        finally
        {
            _backgroundBusy = false;
        }
    }

    /// Installs and relaunches, but never while a dictation is being recorded or transcribed (the Mac's
    /// immediate install would drop it) and never over an open update window.
    private async Task InstallWhenIdleAsync(UpdateEngine engine, AvailableUpdate update, string path)
    {
        while (true)
        {
            if (_dictating || IsUserFlowActive)
            {
                await Task.Delay(WaitForIdlePoll).ConfigureAwait(false);
                continue;
            }

            DebugLog.Write($"Installing update {update}.");
            var outcome = await engine.InstallAsync(update, path).ConfigureAwait(false);
            if (outcome == InstallOutcome.Deferred)
            {
                await Task.Delay(WaitForIdlePoll).ConfigureAwait(false);
                continue;
            }
            if (outcome != InstallOutcome.Started) DebugLog.Write($"Update install did not start: {outcome}");
            return;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Interactive: "Update Required" alert and the "Check for Updates…" window (UI thread)
    // ------------------------------------------------------------------------------------------------

    /// Mac showForceUpdateAlert. "Update Now" runs the normal check window (AppDelegate.checkForUpdates());
    /// "Quit" exits the app.
    private async Task ShowRequiredAlertAsync(string latestVersion)
    {
        if (_requiredAlertShown || IsUserFlowActive) return;
        _requiredAlertShown = true;
        var window = new UpdateWindow();
        try
        {
            var choice = await window.PresentAsync(new UpdateView(
                Title: "Update Required",
                Message: $"{AppDisplayName} {latestVersion} is required to continue. The app will now update.",
                Buttons: new[] { new UpdateButton("Update Now", IsDefault: true), new UpdateButton("Quit") },
                Closable: false));

            CloseQuietly(window);
            if (choice == 0) await RunUserFlowAsync();
            else App.Quit();
        }
        finally
        {
            _requiredAlertShown = false;
        }
    }

    private async Task RunUserFlowAsync()
    {
        if (_window != null)
        {
            _window.Raise();
            return;
        }

        var window = _window = new UpdateWindow();
        try
        {
            await ShowCheckFlowAsync(window);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Update window failed: {ex}");
            try { await window.PresentAsync(ErrorView("An error occurred while running the updater. Please try again later.")); }
            catch (Exception) { /* the window is gone */ }
        }
        finally
        {
            _window = null;
            CloseQuietly(window);
        }
    }

    private async Task ShowCheckFlowAsync(UpdateWindow window)
    {
        if (!IsConfigured)
        {
            await window.PresentAsync(ErrorView("Updates are not set up in this build."));
            return;
        }

        var engine = await GetEngineAsync();
        using var cancel = new CancellationTokenSource();

        // Sparkle shows the "Checking for updates…" window only if the check takes a moment.
        var stopped = window.PresentAsync(new UpdateView("Checking for updates…", Progress: UpdateProgressMode.Indeterminate,
            Buttons: new[] { new UpdateButton("Cancel") }), raise: false);
        var check = engine.CheckAsync(ignoreSkippedVersions: true, cancel.Token);
        if (await Task.WhenAny(check, Task.Delay(400)) != check) window.Raise();
        if (await Task.WhenAny(check, stopped) == stopped)
        {
            cancel.Cancel();
            return;
        }

        var result = await check;
        LastCheckTime = engine.LastCheckTime;
        switch (result.Status)
        {
            case UpdateCheckStatus.Failed:
                DebugLog.Write($"Update check failed: {result.Error}");
                await window.PresentAsync(ErrorView("An error occurred in retrieving update information. Please try again later."));
                break;

            case UpdateCheckStatus.UpToDate:
                await window.PresentAsync(new UpdateView(
                    Title: "You’re up to date!",
                    Message: $"{AppDisplayName} {ApiConstants.AppVersion} is currently the newest version available.",
                    Buttons: new[] { new UpdateButton("OK", IsDefault: true) }));
                break;

            default:
                await OfferUpdateAsync(window, engine, result);
                break;
        }
    }

    /// Sparkle's SUUpdateAlert: title, "now available - you have" line, release notes, and the three choices.
    private async Task OfferUpdateAsync(UpdateWindow window, UpdateEngine engine, UpdateCheckResult result)
    {
        var update = result.Latest!;
        var critical = result.AnyCritical;
        var (offered, running) = DisplayVersions(update);

        var notes = ReleaseNotesParser.Parse(await ReleaseNotesFor(update));
        var buttons = new List<UpdateButton>();
        if (!critical)   // Sparkle hides "Skip This Version" and "Remind Me Later" for critical updates
        {
            buttons.Add(new UpdateButton("Skip This Version", IsLeading: true));
            buttons.Add(new UpdateButton("Remind Me Later"));
        }
        buttons.Add(new UpdateButton("Install Update", IsDefault: true));

        var download = critical
            ? $"{AppDisplayName} {offered} is now available—you have {running}. This is an important update; would you like to download it now?"
            : $"{AppDisplayName} {offered} is now available—you have {running}. Would you like to download it now?";

        var picked = await window.PresentAsync(new UpdateView(
            Title: critical
                ? $"An important update to {AppDisplayName} is ready to install"
                : $"A new version of {AppDisplayName} is available!",
            Message: download,
            ReleaseNotes: notes,
            Buttons: buttons));

        var choice = picked >= 0 ? buttons[picked].Text : null;
        switch (choice)
        {
            case "Skip This Version":
                engine.SkipVersion(update);
                break;
            case "Install Update":
                await DownloadAndInstallAsync(window, engine, update);
                break;
            // "Remind Me Later", Escape or closing the window: the next background check offers it again.
        }
    }

    private async Task DownloadAndInstallAsync(UpdateWindow window, UpdateEngine engine, AvailableUpdate update)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p =>
        {
            var fraction = p.TotalBytes > 0 ? (double)p.BytesReceived / p.TotalBytes : Math.Clamp(p.Percent / 100.0, 0, 1);
            window.SetProgress(fraction, p.TotalBytes > 0 ? $"{FormatBytes(p.BytesReceived)} of {FormatBytes(p.TotalBytes)}" : FormatBytes(p.BytesReceived));
        });

        var stopped = window.PresentAsync(new UpdateView("Downloading update…", Progress: UpdateProgressMode.Indeterminate,
            Buttons: new[] { new UpdateButton("Cancel") }));
        var download = engine.DownloadAsync(update, progress, cancel.Token);

        if (await Task.WhenAny(download, stopped) == stopped)
        {
            cancel.Cancel();
            try { await download; } catch (Exception) { /* cancelled */ }
            return;
        }

        string path;
        try
        {
            path = await download;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Update download failed: {ex.Message}");
            var message = ex is InvalidDataException
                ? "The update is improperly signed and could not be validated. Please try again later or contact the app developer."
                : "An error occurred while downloading the update. Please try again later.";
            await window.PresentAsync(ErrorView(message));
            return;
        }

        while (true)
        {
            if (_dictating)
            {
                // Not in Sparkle: the installer restarts the app, which would drop a dictation in flight.
                window.Present(new UpdateView("Ready to Install",
                    $"{AppDisplayName} will restart to install the update as soon as your current dictation finishes.",
                    Progress: UpdateProgressMode.Indeterminate));
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                continue;
            }

            window.Present(new UpdateView("Installing update…", Progress: UpdateProgressMode.Indeterminate, Closable: false));
            var outcome = await engine.InstallAsync(update, path);
            switch (outcome)
            {
                case InstallOutcome.Started:
                    return;   // the app is quitting; the window goes with it

                case InstallOutcome.Deferred:
                    continue;

                case InstallOutcome.InvalidSignature:
                    await window.PresentAsync(ErrorView("The update is improperly signed and could not be validated. Please try again later or contact the app developer."));
                    return;

                default:
                    await window.PresentAsync(ErrorView("An error occurred while launching the installer. Please try again later."));
                    return;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private static UpdateView ErrorView(string message) => new(
        Title: "Update Error!",
        Message: message,
        Buttons: new[] { new UpdateButton("Cancel Update", IsDefault: true) });

    /// Sparkle shows "1.2" against "1.3", or "1.2 (6)" against "1.2 (7)" when the short versions are equal.
    private static (string Offered, string Running) DisplayVersions(AvailableUpdate update)
    {
        var running = ApiConstants.AppVersion;
        return string.Equals(update.ShortVersion, running, StringComparison.Ordinal)
            ? ($"{update.ShortVersion} ({update.Build})", $"{running} ({ApiConstants.AppBuild})")
            : (update.ShortVersion, running);
    }

    private static async Task<string> ReleaseNotesFor(AvailableUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.ReleaseNotesHtml)) return update.ReleaseNotesHtml;
        if (!Uri.TryCreate(update.ReleaseNotesUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var text = await http.GetStringAsync(uri);
            return text.Length > 256 * 1024 ? text[..(256 * 1024)] : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} bytes"
            : value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static void CloseQuietly(UpdateWindow window)
    {
        try { window.Close(); }
        catch (Exception) { /* already closed */ }
    }

    private static Task OnUiThreadAsync(Func<Task> action)
    {
        var queue = UiThread.Queue;
        if (queue.HasThreadAccess) return action();

        var done = new TaskCompletionSource();
        if (!queue.TryEnqueue(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
            }))
            done.TrySetException(new InvalidOperationException("The UI thread is not available."));
        return done.Task;
    }

    private Task<UpdateEngine> GetEngineAsync()
    {
        lock (_sync) return _engineTask ??= CreateEngineAsync();
    }

    private async Task<UpdateEngine> CreateEngineAsync()
    {
        var feed = await ResolveFeedUrlAsync().ConfigureAwait(false);
        var engine = new UpdateEngine(new UpdateEngineOptions
        {
            FeedUrl = feed,
            PublicKey = UpdateKeys.Ed25519PublicKey,
            CurrentBuild = ApiConstants.AppBuild,
            StateDirectory = AppPaths.GetPath("updates"),
            UserAgent = $"Coworker-Windows/{ApiConstants.AppVersion} (build {ApiConstants.AppBuild})",
            Log = DebugLog.Write,
            QuitApplication = QuitForInstaller,
            CanQuitNow = () => !_dictating,
        });
        DebugLog.Write($"Updater ready: feed {feed}, running {ApiConstants.AppVersionDisplay}.");
        return engine;
    }

    /// The installer waits for this process to end before it replaces the files.
    private static Task QuitForInstaller()
    {
        UiThread.Post(App.Quit);
        // Do not linger on a stuck shutdown: the installer is waiting for us.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            Environment.Exit(0);
        });
        return Task.CompletedTask;
    }

    private static async Task<string> ResolveFeedUrlAsync()
    {
        var production = $"{ApiConstants.WebBaseUrl}/coworker/appcast-windows.xml";
#if DEBUG
        // Dev only: COWORKER_UPDATE_FEED pins a feed; otherwise Mac UpdaterDelegate.checkLocalFeedAvailability -
        // prefer the local web app (localhost:3000) when it answers and its newest build is newer than production's.
        var pinned = Environment.GetEnvironmentVariable("COWORKER_UPDATE_FEED")?.Trim();
        if (!string.IsNullOrEmpty(pinned) && Uri.TryCreate(pinned, UriKind.Absolute, out _)) return pinned;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var local = await http.GetStringAsync(LocalFeedUrl).ConfigureAwait(false);
            string? remote = null;
            try
            {
                using var prod = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                remote = await prod.GetStringAsync(production).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            var choice = FeedRules.ChooseFeed(FeedRules.LatestBuild(local), remote == null ? null : FeedRules.LatestBuild(remote));
            return choice == FeedRules.FeedChoice.Local ? LocalFeedUrl : production;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return production;
        }
#else
        await Task.CompletedTask.ConfigureAwait(false);
        return production;
#endif
    }
}
