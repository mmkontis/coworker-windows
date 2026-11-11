using Coworker.Services;
using Coworker.Views;

namespace Coworker.Dictation;

/// The message the Mac's UpgradeWindow shows: sign in, or upgrade after the daily limit.
public sealed record UpgradeRequest(string Message, string Url, int? CurrentUsage, int? Limit, string ActionTitle);

/// Where sign-in and quota prompts go. The Upgrade window (shell) replaces <see cref="Show"/>; until it
/// exists a toast with a button opens the page.
public static class UpgradePrompt
{
    public static Action<UpgradeRequest> Show { get; set; } = request =>
        ToastService.Show(ToastKind.Warning, null, request.Message, 10, request.ActionTitle,
            () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(request.Url)));
}

/// The real toasts, prompts and earcons behind <see cref="IDictationOutput"/>.
internal sealed class DictationOutput : IDictationOutput
{
    public void Toast(ToastKind kind, string message, double seconds) =>
        ToastService.Show(kind, null, message, seconds);

    public void SignInRequired(string message, string url) =>
        UpgradePrompt.Show(new UpgradeRequest(message, url, null, null, "Connect"));

    public void QuotaExceeded(string message, int? currentUsage, int? limit, string url) =>
        UpgradePrompt.Show(new UpgradeRequest(message, url, currentUsage, limit, "Upgrade"));

    public void MicrophoneAccessNeeded() => MicrophonePermission.ShowPermissionRequired();

    public void PlayStartSound() => DictationSounds.Shared.PlayStart();

    public void PlayStopSound() => DictationSounds.Shared.PlayStop();
}
