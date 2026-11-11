using Coworker.Dictation;

namespace Coworker.Services;

/// What the Record page shows that outlives the page: the latest transcription and what the app says
/// about dictation right now (Mac: transcriptionText and permissionStatusMessage of the view model).
/// Fed by <see cref="DictationController"/>.
public static class TranscribeState
{
    public static string LatestText { get; private set; } = "";

    /// A short note under the record button: "Recording...", why the microphone could not be opened, ...
    public static string StatusMessage { get; private set; } = "";

    /// Raised on the UI thread when either value changed.
    public static event Action? Changed;

    /// Starts following the dictation controller. Call once at startup, before the controller's other
    /// result handlers are added, so a transcript that a voice command consumes still shows here.
    public static void Start()
    {
        var dictation = DictationController.Shared;
        dictation.AddResultHandler(new LatestTextHandler());
        dictation.StatusChanged += status =>
        {
            StatusMessage = status;
            Changed?.Invoke();
        };
    }

    /// Sees every transcript and never consumes it.
    private sealed class LatestTextHandler : IDictationResultHandler
    {
        public Task<bool> TryHandleAsync(string transcript)
        {
            LatestText = transcript;
            Changed?.Invoke();
            return Task.FromResult(false);
        }
    }
}
