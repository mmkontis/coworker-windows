using Coworker.Services;
using Coworker.Transcription;

namespace Coworker.Dictation;

public enum DictationState
{
    Idle,

    /// The shortcut went down and the microphone is being opened; the bar already shows recording.
    Starting,

    Recording,

    /// At least one take is being transcribed.
    Transcribing,

    /// Words are being put into the target field.
    Pasting,
}

/// What a take is for: typing the words, or asking the AI and typing its answer (Mac: RecordingMode
/// transcription / chatCompletion).
public enum DictationMode
{
    Dictation,

    /// Chat completion: the transcript goes to the AI and its answer is pasted instead.
    Chat,
}

/// Something that consumes a transcript before it is pasted (a voice command that ran, for example).
/// Handlers are asked in registration order, one at a time, on the UI thread; the first to return true
/// consumes the text and nothing is pasted. A handler may take a moment to decide (the bar keeps spinning).
public interface IDictationResultHandler
{
    Task<bool> TryHandleAsync(string transcript);
}

/// Where the words went.
public enum PasteOutcome
{
    /// The text is in the field (possibly followed by Return).
    Inserted,

    /// Not pasted (no usable field, a password field): the text is on the clipboard.
    ClipboardOnly,

    /// A take started recording before the keys went out (sending them would end it). Nothing was typed;
    /// the caller pastes the text again once that take ends.
    Deferred,

    /// The target app runs as administrator, so Windows would not let this app send it keys; the text is on the clipboard.
    ElevatedTarget,

    /// The field provably did not change even after a second attempt; the text is on the clipboard.
    Unconfirmed,
}

/// The result of one paste; <see cref="Method"/> and <see cref="AutoSubmitted"/> feed telemetry.
public sealed record PasteResult(PasteOutcome Outcome, string? TargetApp, string Method, bool AutoSubmitted = false);

/// Where a paste will go, decided while the text was waiting (opaque to the controller).
public interface IPasteDestination { }

/// Puts finished text into the field the user was dictating into (Mac: DictationPasteController).
public interface IDictationPaster
{
    /// The field the words would go to right now. Taken when a result has to wait behind a take being recorded.
    IPasteDestination? CaptureDestination();

    /// Pastes one at a time, in call order. Never sends keys while a take is being recorded.
    Task<PasteResult> PasteAsync(string text, IPasteDestination? destination);

    /// The field a take starting now will most likely be typed into, described for the server (never its
    /// value), or null when there is none or it is a password field. Called when a dictation take starts.
    Task<TakeField?> ReadTakeFieldAsync() => Task.FromResult<TakeField?>(null);
}

/// What the controller shows and plays; kept apart so the controller has no UI in it.
public interface IDictationOutput
{
    void Toast(ToastKind kind, string message, double seconds);

    /// Nobody is signed in; the user has to connect the app to their account.
    void SignInRequired(string message, string url);

    /// The daily limit is used up.
    void QuotaExceeded(string message, int? currentUsage, int? limit, string url);

    /// Windows blocks the microphone for this app.
    void MicrophoneAccessNeeded();

    void PlayStartSound();

    void PlayStopSound();
}
