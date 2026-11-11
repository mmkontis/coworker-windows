using System.ComponentModel;
using NetSparkleUpdater;
using NetSparkleUpdater.Configurations;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Events;

namespace Coworker.Services.Updates;

public sealed record UpdateEngineOptions
{
    /// Sparkle-format appcast with the Windows items (https://humanlike.co/coworker/appcast-windows.xml).
    public required string FeedUrl { get; init; }

    /// Base64 Ed25519 public key that verifies every installer (see installer/README.md).
    public required string PublicKey { get; init; }

    /// Running build number (sparkle:version), what "newer" means.
    public required string CurrentBuild { get; init; }

    /// Where the skipped-version / last-check state and the downloaded installers live.
    public required string StateDirectory { get; init; }

    /// Inno Setup switches for the silent update (they are not in the feed: NetSparkle has no per-item arguments).
    public string InstallerArguments { get; init; } = DefaultInstallerArguments;

    public string UserAgent { get; init; } = "Coworker-Windows";

    public TimeSpan FeedTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public Action<string>? Log { get; init; }

    /// Called when the installer has been launched and the app has to exit so it can replace the files.
    public Func<Task>? QuitApplication { get; init; }

    /// Return false to keep the app running (dictation in progress): the install is deferred.
    public Func<bool>? CanQuitNow { get; init; }

    /// Test seam: the process the launcher script waits for before starting the installer (default: this process).
    public string? ProcessIdToWaitFor { get; init; }

    /// The CPU flavour of this build (default: the running process), used to pick windows-x64 / windows-arm64 items.
    public System.Runtime.InteropServices.Architecture? Architecture { get; init; }

    public const string DefaultInstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /CLOSEAPPLICATIONS";
}

/// <summary>
/// The UI-free updater: NetSparkle (Sparkle-format feed, Ed25519 verification, download, installer
/// launch) behind a small API. Windows twin of Sparkle's SPUUpdater as the Mac app configures it.
/// </summary>
public sealed class UpdateEngine : IDisposable
{
    private const int DownloadStallSeconds = 45;

    private readonly UpdateEngineOptions _options;
    private readonly SparkleUpdater _updater;
    private readonly HttpAppCastDownloader _feedDownloader;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly string _downloadDirectory;
    private InstallUpdateFailureReason? _installFailure;

    public UpdateEngine(UpdateEngineOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.StateDirectory);
        _downloadDirectory = Path.Combine(options.StateDirectory, "downloads");
        Directory.CreateDirectory(_downloadDirectory);

        var accessor = new FixedAssemblyAccessor(options.CurrentBuild, "Coworker");
        _feedDownloader = new HttpAppCastDownloader(options.UserAgent, options.FeedTimeout);
        _updater = new SparkleUpdater(options.FeedUrl, new Ed25519DownloadVerifier(options.PublicKey))
        {
            // We draw the UI ourselves (Views/UpdateWindow); NetSparkle only does the protocol work.
            UIFactory = null,
            UserInteractionMode = UserInteractionMode.DownloadNoInstall,
            // The Inno installer restarts the app itself ([Run] entry for silent installs).
            RelaunchAfterUpdate = false,
            CustomInstallerArguments = options.InstallerArguments,
            LogWriter = new DelegateLogger(options.Log),
            Configuration = new JSONConfiguration(accessor, Path.Combine(options.StateDirectory, "update-state.json")),
            AppCastDataDownloader = _feedDownloader,
            TmpDownloadFilePath = _downloadDirectory,
            CheckServerFileName = false,
        };
        // NetSparkle matches "windows*" but not the CPU: keep x64/arm64/x86 items apart and drop older builds.
        _updater.AppCastHelper.AppCastFilter = new WindowsBuildFilter(options.Architecture ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        if (!string.IsNullOrWhiteSpace(options.ProcessIdToWaitFor))
            _updater.ProcessIDToKillBeforeInstallerRuns = options.ProcessIdToWaitFor;

        LoadLastCheckTime();

        _updater.PreparingToExitAsync += OnPreparingToExit;
        _updater.CloseApplicationAsync += OnCloseApplication;
        _updater.InstallUpdateFailed += (reason, _) =>
        {
            _installFailure = reason;
            return true;   // the return value is unused (NetSparkle 3.x quirk)
        };
    }

    /// The appcast being read. Dev builds may switch it (local feed preferred when newer).
    public string FeedUrl
    {
        get => _updater.AppCastUrl;
        set => _updater.AppCastUrl = value;
    }

    public string DownloadDirectory => _downloadDirectory;

    /// When a check last reached the feed and understood it (persisted across launches; drives the hourly cadence).
    public DateTimeOffset? LastCheckTime { get; private set; }

    private string LastCheckFile => Path.Combine(_options.StateDirectory, "last-check.txt");

    private void LoadLastCheckTime()
    {
        try
        {
            if (File.Exists(LastCheckFile) &&
                DateTimeOffset.TryParse(File.ReadAllText(LastCheckFile).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                LastCheckTime = time;
        }
        catch (IOException)
        {
        }
    }

    private void RecordCheckTime()
    {
        LastCheckTime = DateTimeOffset.UtcNow;
        try { File.WriteAllText(LastCheckFile, LastCheckTime.Value.ToString("O")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// Fetches the feed and reports what is newer than the running build. Never throws for feed problems.
    /// <paramref name="recordCheckTime"/> false for probes that must not postpone the scheduled check.
    public async Task<UpdateCheckResult> CheckAsync(bool ignoreSkippedVersions, CancellationToken ct = default, bool recordCheckTime = true)
    {
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await _updater.CheckForUpdatesQuietly(ignoreSkippedVersions).WaitAsync(ct).ConfigureAwait(false);
            switch (info.Status)
            {
                case UpdateStatus.CouldNotDetermine:
                    return new UpdateCheckResult { Status = UpdateCheckStatus.Failed, Error = "The update feed could not be read." };

                case UpdateStatus.UpdateNotAvailable:
                    if (recordCheckTime) RecordCheckTime();
                    return new UpdateCheckResult { Status = UpdateCheckStatus.UpToDate };

                default:
                    if (recordCheckTime) RecordCheckTime();
                    var taggedCritical = FeedRules.CriticalTaggedBuilds(_feedDownloader.LastFeed);
                    var updates = info.Updates
                        .Select(item => new AvailableUpdate(item, item.IsCriticalUpdate || taggedCritical.Contains(item.Version ?? "")))
                        .ToList();
                    return new UpdateCheckResult
                    {
                        Status = info.Status == UpdateStatus.UserSkipped ? UpdateCheckStatus.Skipped : UpdateCheckStatus.UpdateAvailable,
                        Latest = updates.FirstOrDefault(),
                        Updates = updates,
                    };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Update check failed: {ex.Message}");
            return new UpdateCheckResult { Status = UpdateCheckStatus.Failed, Error = ex.Message };
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// "Skip This Version": background checks stop offering this build (a manual check still does).
    public void SkipVersion(AvailableUpdate update) => _updater.Configuration.SetVersionToSkip(update.Build);

    /// Downloads the installer and verifies its Ed25519 signature; returns the local path.
    /// Throws (and deletes the file) when the download fails or the signature is wrong.
    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            throw new InvalidOperationException("The update has no download link.");

        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastActivity = DateTime.UtcNow;

        void OnProgress(object? sender, AppCastItem item, ItemDownloadProgressEventArgs e)
        {
            lastActivity = DateTime.UtcNow;
            progress?.Report(new DownloadProgress(e.BytesReceived, e.TotalBytesToReceive, e.ProgressPercentage));
        }
        void OnStarted(AppCastItem item, string path) => lastActivity = DateTime.UtcNow;
        void OnFinished(AppCastItem item, string path) => completion.TrySetResult(path);
        void OnCorrupt(AppCastItem item, string path) => completion.TrySetException(new InvalidDataException("The downloaded update failed signature verification."));
        void OnError(AppCastItem item, string? path, Exception error) => completion.TrySetException(error);
        void OnCanceled(AppCastItem item, string path) => completion.TrySetCanceled();

        _updater.DownloadMadeProgress += OnProgress;
        _updater.DownloadStarted += OnStarted;
        _updater.DownloadFinished += OnFinished;
        _updater.DownloadedFileIsCorrupt += OnCorrupt;
        _updater.DownloadHadError += OnError;
        _updater.DownloadCanceled += OnCanceled;
        try
        {
            _updater.TmpDownloadFileNameWithExtension = InstallerFileName(update);
            using var cancelRegistration = ct.Register(() => _updater.CancelFileDownload());

            await _updater.InitAndBeginDownload(update.Raw).ConfigureAwait(false);

            while (!completion.Task.IsCompleted)
            {
                await Task.WhenAny(completion.Task, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    _updater.CancelFileDownload();
                    completion.TrySetCanceled(ct);
                }
                else if ((DateTime.UtcNow - lastActivity).TotalSeconds > DownloadStallSeconds)
                {
                    _updater.CancelFileDownload();
                    completion.TrySetException(new TimeoutException("The update download stalled."));
                }
            }
            try
            {
                return await completion.Task.ConfigureAwait(false);
            }
            catch
            {
                // Never leave a corrupt or half-written installer lying around.
                DeleteQuietly(Path.Combine(_downloadDirectory, InstallerFileName(update)));
                throw;
            }
        }
        finally
        {
            _updater.DownloadMadeProgress -= OnProgress;
            _updater.DownloadStarted -= OnStarted;
            _updater.DownloadFinished -= OnFinished;
            _updater.DownloadedFileIsCorrupt -= OnCorrupt;
            _updater.DownloadHadError -= OnError;
            _updater.DownloadCanceled -= OnCanceled;
            _downloadGate.Release();
        }
    }

    /// Verifies the signature again, launches the installer and asks the app to quit. The installer
    /// waits for this process to exit, runs silently and starts the new build.
    public async Task<InstallOutcome> InstallAsync(AvailableUpdate update, string installerPath)
    {
        _installFailure = null;
        _declined = false;
        await _updater.InstallUpdate(update.Raw, installerPath).ConfigureAwait(false);
        if (_declined) return InstallOutcome.Deferred;
        return _installFailure switch
        {
            null => InstallOutcome.Started,
            InstallUpdateFailureReason.InvalidSignature => InstallOutcome.InvalidSignature,
            InstallUpdateFailureReason.FileNotFound => InstallOutcome.FileNotFound,
            _ => InstallOutcome.Failed,
        };
    }

    /// Deletes downloaded installers, keeping <paramref name="keepPath"/> when given.
    public void CleanUpDownloads(string? keepPath = null)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_downloadDirectory))
            {
                if (keepPath != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                DeleteQuietly(file);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool _declined;

    private Task OnPreparingToExit(object? sender, CancelEventArgs e)
    {
        if (_options.CanQuitNow is { } canQuit && !canQuit())
        {
            e.Cancel = true;
            _declined = true;
        }
        return Task.CompletedTask;
    }

    private async Task OnCloseApplication()
    {
        if (_options.QuitApplication != null) await _options.QuitApplication().ConfigureAwait(false);
    }

    /// "Coworker-1.2.0-7.exe": one file per build; the name never comes from the server.
    private static string InstallerFileName(AvailableUpdate update)
    {
        static string Clean(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return $"Coworker-{Clean(update.ShortVersion)}-{Clean(update.Build)}.exe";
    }

    public void Dispose()
    {
        _updater.PreparingToExitAsync -= OnPreparingToExit;
        _updater.CloseApplicationAsync -= OnCloseApplication;
        _updater.Dispose();
        _checkGate.Dispose();
        _downloadGate.Dispose();
    }
}
