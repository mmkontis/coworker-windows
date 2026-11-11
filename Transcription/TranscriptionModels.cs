using System.Text.Json;
using System.Text.Json.Serialization;
using Coworker.Dictation;
using Coworker.Services;

namespace Coworker.Transcription;

/// A word the transcriber should know: a dictionary entry, or a voice-command phrase (word only).
/// The server applies the replacements and shortcuts; nothing is substituted on this side.
public sealed record DictionaryEntryPayload(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("word")] string Word,
    [property: JsonPropertyName("definition")] string? Definition = null,
    [property: JsonPropertyName("shortcut")] string? Shortcut = null);

/// Everything a take needs to be transcribed, fixed when it starts.
/// The per-take prompt is built on the server from <see cref="TranslateTo"/>, <see cref="CustomPrompt"/> and
/// <see cref="ActiveApp"/> (Mac: buildEffectivePrompt), so no prompt text is sent from here.
/// <see cref="TextRules"/> asks the server to type spoken "slash / dot / dash" as / . - and to lowercase web
/// addresses; it is on for dictation and off for a chat question (Mac: only plain transcription gets them).
public sealed record TranscriptionContext(
    string RequestId,
    string DeviceId,
    string Model,
    string Language,
    bool TranslationEnabled,
    string TranslationLanguage,
    string CustomPrompt,
    string ActiveApp,
    bool TextRules,
    IReadOnlyList<DictionaryEntryPayload> Dictionary)
{
    /// The language to translate into, or null when translation is off.
    public string? TranslateTo => TranslationEnabled && TranslationLanguage.Length > 0 ? TranslationLanguage : null;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// The language the HTTP upload asks for: the target when translating (Mac: performTranscription).
    /// The WebSocket start message always sends the chosen language and asks for the translation with translate_to.
    public string HttpLanguage => TranslationEnabled ? TranslationLanguage : Language;

    /// The dictionary as the JSON array the HTTP form carries, or null when there are no entries.
    public string? DictionaryJson => Dictionary.Count == 0 ? null : JsonSerializer.Serialize(Dictionary, Json);

    /// The field the take will be typed into, read over UI Automation when a dictation take starts (null for a
    /// chat question). The server formats the words for it (<see cref="TakeField"/>).
    public Task<TakeField?>? FieldRead { get; init; }

    /// The field, once <see cref="FieldRead"/> has finished; null before that, or when there is none.
    public TakeField? Field => FieldRead is { IsCompletedSuccessfully: true } read ? read.Result : null;

    /// The field as the JSON string the HTTP form carries, or null.
    public string? FieldJson => Field?.ToJson();
}

/// One take on its way to text. Engines record which route it took (for telemetry and retry fields).
public sealed class TranscriptionJob
{
    public TranscriptionJob(TranscriptionContext context, CapturedAudio audio, ITranscriptionStream? stream, double timeoutBudgetMs)
    {
        Context = context;
        Audio = audio;
        Stream = stream;
        TimeoutBudgetMs = timeoutBudgetMs;
    }

    public TranscriptionContext Context { get; }
    public CapturedAudio Audio { get; }

    /// The live streaming session opened when the take started, if the engine has one.
    public ITranscriptionStream? Stream { get; }

    public double TimeoutBudgetMs { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// "websocket" or "http" (or an engine's own name).
    public string Transport { get; set; } = "http";

    /// How many times the take was sent again after a failure; 1 after the HTTP fallback.
    public int RetryCount { get; set; }
    public string? RetryTrigger { get; set; }
}

public enum TranscriptionFailure
{
    /// The session is gone, or nobody is signed in.
    SignInRequired,

    /// The daily limit is used up (429).
    QuotaExceeded,

    /// The server refused the take with a message the user should read as it is.
    ServerMessage,

    /// Network, server or response trouble; the message is the API's error text.
    Failed,

    Canceled,
}

public sealed record TranscriptionError(TranscriptionFailure Kind, string Message, ApiException? Api = null);

public sealed record TranscriptionOutcome(string? Text, TranscriptionError? Error)
{
    public static TranscriptionOutcome Success(string text) => new(text, null);

    public static TranscriptionOutcome Fail(TranscriptionFailure kind, string message, ApiException? api = null) =>
        new(null, new TranscriptionError(kind, message, api));
}

/// A live connection that takes audio while the user is still speaking.
public interface ITranscriptionStream
{
    /// Audio as it is captured (16 kHz int16 mono). Any thread.
    void Feed(ReadOnlyMemory<byte> pcm);

    /// The server refused the take while it was being recorded (UI thread). The take should end with the message.
    event Action<string>? Failed;

    /// Drops the take on the server side and stops listening.
    void Cancel(string reason);
}

/// A way to turn a recorded take into text. The first engine in <see cref="TranscriptionEngines"/> that
/// accepts the context handles the take.
public interface ITranscriptionEngine
{
    bool Accepts(TranscriptionContext context);

    /// Called when recording starts; return a session to stream audio into, or null to transcribe the finished recording.
    ITranscriptionStream? OpenStream(TranscriptionContext context);

    Task<TranscriptionOutcome> TranscribeAsync(TranscriptionJob job, CancellationToken ct);
}

/// The ordered engines. The Coworker server is last and accepts everything; a later slice (OpenRouter
/// with the user's own key, local ASR) registers its engine ahead of it.
public static class TranscriptionEngines
{
    private static readonly List<ITranscriptionEngine> Engines = new() { new ServerTranscriptionEngine() };

    /// Adds an engine ahead of the server engine; engines are asked in the order they were registered.
    public static void Register(ITranscriptionEngine engine) => Engines.Insert(Engines.Count - 1, engine);

    public static ITranscriptionEngine For(TranscriptionContext context) =>
        Engines.First(engine => engine.Accepts(context));
}
