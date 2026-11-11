using NetSparkleUpdater;

namespace Coworker.Services.Updates;

public enum UpdateCheckStatus
{
    /// The feed has nothing newer than the running build.
    UpToDate,
    /// A newer build exists (see <see cref="UpdateCheckResult.Latest"/>).
    UpdateAvailable,
    /// A newer build exists but the user chose "Skip This Version" (background checks stay quiet).
    Skipped,
    /// The feed could not be fetched or parsed.
    Failed,
}

/// <summary>One release from the feed that is newer than the running build and installable on this machine.</summary>
public sealed class AvailableUpdate
{
    internal AvailableUpdate(AppCastItem item, bool isCritical)
    {
        Raw = item;
        IsCritical = isCritical;
    }

    internal AppCastItem Raw { get; }

    /// sparkle:version, the build number the updater compares (Mac CFBundleVersion).
    public string Build => Raw.Version ?? "";

    /// sparkle:shortVersionString, what the user sees (Mac CFBundleShortVersionString).
    public string ShortVersion => string.IsNullOrWhiteSpace(Raw.ShortVersion) ? Build : Raw.ShortVersion!;

    public string Title => Raw.Title ?? "";

    /// The item's HTML release notes (its description). Empty when the feed only links to notes.
    public string ReleaseNotesHtml => Raw.Description ?? "";

    /// sparkle:releaseNotesLink, when the notes live at a URL instead of inline.
    public string ReleaseNotesUrl => Raw.ReleaseNotesLink ?? "";

    public string DownloadUrl => Raw.DownloadLink ?? "";

    public long Length => Raw.UpdateSize;

    public DateTimeOffset? PublishedAt => Raw.PublicationDate == default ? null : new DateTimeOffset(Raw.PublicationDate);

    /// sparkle:criticalUpdate (or sparkle:tag critical): the app must update ("Update Required").
    public bool IsCritical { get; }

    public override string ToString() => $"{ShortVersion} ({Build})";
}

public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }

    /// The newest installable release; set for <see cref="UpdateCheckStatus.UpdateAvailable"/> and
    /// <see cref="UpdateCheckStatus.Skipped"/>.
    public AvailableUpdate? Latest { get; init; }

    /// Every installable release newer than the running build, newest first.
    public IReadOnlyList<AvailableUpdate> Updates { get; init; } = Array.Empty<AvailableUpdate>();

    /// True when any release the user would be jumping over is critical (Mac: the feed marks a
    /// newer release with sparkle:criticalUpdate).
    public bool AnyCritical => Updates.Any(u => u.IsCritical);

    public string? Error { get; init; }
}

public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes, int Percent);

public enum InstallOutcome
{
    /// The installer was launched and the app is quitting so it can run.
    Started,
    /// The app declined to quit right now (recording in progress); nothing was launched.
    Deferred,
    /// The installer's signature did not verify; nothing was launched.
    InvalidSignature,
    /// The downloaded installer is gone; nothing was launched.
    FileNotFound,
    /// The installer could not be started; nothing was launched.
    Failed,
}
