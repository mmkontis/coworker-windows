using CommunityToolkit.Mvvm.Messaging;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Paste;
using Coworker.Services;
using Coworker.Transcription;

namespace Coworker.Dictation;

/// <summary>
/// One dictation from shortcut to pasted text (Mac: the recording half of ContentViewModel plus the
/// paste hand-off in AppDelegate). Idle, Starting, Recording, Transcribing, Pasting and back, with
/// Cancel from anywhere. Takes may overlap: a new take can start while an earlier one is still being
/// transcribed, and results that arrive while a take is being recorded wait until it ends (pressing keys
/// for a paste mid-take would disturb the shortcut). UI thread only.
/// </summary>
public sealed class DictationController
{
    public static DictationController Shared { get; } = new(
        AudioService.Shared, PasteEngine.Shared, new DictationOutput());

    private const int MinWavBytes = 1000;
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(0.6);   // Whisper hallucinates on shorter clips

    private readonly IAudioCapture _audio;
    private readonly IDictationPaster _paster;
    private readonly IDictationOutput _output;
    private readonly List<IDictationResultHandler> _handlers = new();
    private readonly List<Job> _jobs = new();
    private readonly List<(string Text, IPasteDestination? Destination, TranscriptionJob Job)> _queuedPastes = new();
    private readonly LatencyTracker _latency = new();

    private Take? _take;
    private volatile ITranscriptionStream? _stream;
    private volatile float _level;
    private DictationState _state;
    private string _status = "";

    public DictationController(IAudioCapture audio, IDictationPaster paster, IDictationOutput output)
    {
        _audio = audio;
        _paster = paster;
        _output = output;
    }

    /// Whether the microphone may be opened; replaced in tests.
    public Func<bool> IsMicrophoneAllowed { get; init; } = MicrophoneConsent.IsAllowed;

    /// Whether a session exists; replaced in tests.
    public Func<bool> IsSignedIn { get; init; } = () => SessionService.Shared.IsAuthenticated;

    public DictationState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    /// A take is being opened or recorded: no keys may be sent to other apps now.
    public bool IsCaptureActive => _take != null;

    /// Input level 0..1 of the take being recorded; read by the bar's animation.
    public float Level => _level;

    /// The Mac's permissionStatusMessage: what the app says about dictation right now.
    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    /// Escape may cancel: a take is being recorded or transcribed.
    public bool IsCancelable => State is DictationState.Starting or DictationState.Recording or DictationState.Transcribing;

    /// What the take being recorded, or else the latest one still in flight, is for; the bar's colour
    /// follows it. Current by the time <see cref="StateChanged"/> fires.
    public DictationMode Mode { get; private set; }

    public event Action<DictationState>? StateChanged;
    public event Action<string>? StatusChanged;

    /// Adds a handler that sees every transcript before it is pasted.
    public void AddResultHandler(IDictationResultHandler handler) => _handlers.Add(handler);

    // ===== Start =====

    /// The shortcut went down: open the microphone and start listening.
    public void Start(DictationMode mode = DictationMode.Dictation)
    {
        if (_take != null) return;   // already starting or recording

        if (!IsSignedIn())
        {
            Status = "Please sign in to use transcription";
            _output.SignInRequired("Please connect your Windows app to your web account to use transcription features.", ApiConstants.CoworkerConnectUrl);
            return;
        }

        if (!IsMicrophoneAllowed())
        {
            Status = "Microphone permission denied. Enable in Settings.";
            _output.MicrophoneAccessNeeded();
            return;
        }

        var settings = AppSettings.Instance;

        // With stacking, a take still waiting for its answer keeps waiting: the server finished reading
        // its audio at "stop" and answers it before the new take, tagged with its request id. Moving it to
        // HTTP here transcribed it twice (once per path), and both answers could be pasted. Without
        // stacking the earlier take is dropped, as before.
        if (!settings.StackOverlappingRecordings)
        {
            foreach (var job in _jobs.Where(j => j.Phase == DictationState.Transcribing).ToList())
                CancelJob(job);
        }

        // The field the words will go into is read in the background (UI Automation may wait on the other app);
        // the take carries it to the server, which formats the words for it. A chat answer is not fitted.
        var context = BuildContext(settings, mode) with
        {
            FieldRead = mode == DictationMode.Dictation ? _paster.ReadTakeFieldAsync() : null,
        };
        var engine = TranscriptionEngines.For(context);
        var take = _take = new Take(context, engine.OpenStream(context), mode);
        _stream = take.Stream;
        _level = 0;
        if (take.Stream != null) take.Stream.Failed += message => OnStreamFailed(take, message);

        _audio.LevelChanged += OnLevel;
        _audio.Frame += OnFrame;
        Status = "Recording...";
        _output.PlayStartSound();
        Refresh();

        _ = OpenMicrophoneAsync(take);
    }

    private async Task OpenMicrophoneAsync(Take take)
    {
        var error = await _audio.StartAsync();
        if (take.Ended) return;

        if (error == null)
        {
            take.MicrophoneOpen = true;
            Refresh();
            return;
        }

        DebugLog.Write($"Microphone not available: {error}");
        EndTake(take, discardStream: true, "mic_error");
        _output.Toast(ToastKind.Error, error == AudioStartError.NoDevice ? "Mic unavailable. Try again." : "Could not access mic. Try again.", 4);
        Status = error == AudioStartError.NoDevice ? "Mic unavailable. Try again." : "Could not access mic. Try again.";
    }

    private void OnStreamFailed(Take take, string message)
    {
        if (take.Ended) return;

        // The server refused the take while it was being recorded.
        _audio.Abort();
        EndTake(take, discardStream: true, "server_error");
        Status = message;
        _output.Toast(ToastKind.Error, message, message.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? 5 : 3);
    }

    // ===== Stop =====

    /// The shortcut was released: stop listening and transcribe what was said.
    public async Task StopAsync()
    {
        var take = _take;
        if (take == null || take.Stopping) return;
        take.Stopping = true;

        _audio.LevelChanged -= OnLevel;
        _audio.Frame -= OnFrame;
        var captured = await _audio.StopAsync();
        if (take.Ended) return;   // cancelled or failed while the microphone was closing

        take.Ended = true;
        _take = null;
        _stream = null;
        _level = 0;

        if (captured == null || captured.Wav.Length <= MinWavBytes)
        {
            take.Stream?.Cancel("no_audio");
            Status = "No audio recorded";
            Refresh();
            DrainQueuedPastes();
            return;
        }

        if (captured.Duration < MinDuration)
        {
            take.Stream?.Cancel("too_short");
            Status = "Recording too short";
            _output.Toast(ToastKind.Info, "Too short. Try again.", 4.5);
            Refresh();
            DrainQueuedPastes();
            return;
        }

        var budget = AdaptiveTimeout.BudgetMs(captured.Duration.TotalMilliseconds, _latency.P95Ms);
        var job = new Job(new TranscriptionJob(take.Context, captured, take.Stream, budget), take.Mode);
        _jobs.Add(job);
        Status = "Transcribing...";
        Refresh();
        DrainQueuedPastes();

        await RunJobAsync(job);
    }

    // ===== Transcription =====

    private async Task RunJobAsync(Job job)
    {
        var transcription = job.Transcription;
        var engine = TranscriptionEngines.For(transcription.Context);

        TranscriptionOutcome outcome;
        try
        {
            outcome = await engine.TranscribeAsync(transcription, job.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = TranscriptionOutcome.Fail(TranscriptionFailure.Canceled, "");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Transcription engine failed: {ex}");
            outcome = TranscriptionOutcome.Fail(TranscriptionFailure.Failed, ex.Message);
        }

        if (job.Canceled || outcome.Error?.Kind == TranscriptionFailure.Canceled)
        {
            FinishJob(job);
            return;
        }

        var elapsed = (DateTime.UtcNow - transcription.StartedAt).TotalMilliseconds;
        if (outcome.Error != null)
        {
            DictationTelemetry.Send("coworker.client.response.failed", "response", "error", transcription, transcription.Context,
                durationMs: elapsed, error: outcome.Error.Message);
            FinishJob(job);
            ShowFailure(outcome.Error);
            return;
        }

        var text = outcome.Text ?? "";
        _latency.Add(elapsed);
        DictationTelemetry.Send("coworker.client.response.received", "response", "ok", transcription, transcription.Context,
            durationMs: elapsed, metadata: new Dictionary<string, object> { ["text_length"] = text.Length });
        Status = "Transcription complete";
        WeakReferenceMessenger.Default.Send(new StatsChangedMessage());
        job.ResponseReceivedAt = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(text))
        {
            FinishJob(job);
            return;
        }

        if (job.Mode == DictationMode.Chat)
        {
            // Chat completion: what was said is a question; its answer is what gets typed. Voice
            // commands don't apply (Mac: onTranscriptionComplete skips them in chat mode).
            var answer = await AnswerAsync(job, text);
            if (answer == null || job.Canceled)
            {
                FinishJob(job);
                return;
            }
            text = answer;
        }
        else foreach (var handler in _handlers)
        {
            var consumed = await handler.TryHandleAsync(text);
            if (!consumed && !job.Canceled) continue;   // cancelled while the handler decided: nothing is pasted either
            FinishJob(job);
            return;
        }

        if (AppSettings.Instance.StackOverlappingRecordings && IsCaptureActive)
        {
            // Sending keys now would disturb the take being recorded; the paste runs when it ends.
            DebugLog.Write($"Capture active: holding {text.Length} characters until it ends");
            _queuedPastes.Add((text, _paster.CaptureDestination(), transcription));
            FinishJob(job);
            return;
        }

        job.Phase = DictationState.Pasting;
        Refresh();
        await PasteAsync(text, null, job);
        FinishJob(job);
    }

    // The AI's answer to the transcript, or null (cancelled, failed or empty, with a toast for the last two).
    private async Task<string?> AnswerAsync(Job job, string question)
    {
        Status = "Waiting for AI response...";
        var custom = AppSettings.Instance.CustomPrompt;
        var instructions = string.IsNullOrWhiteSpace(custom)
            ? "You are a helpful assistant. Provide concise and accurate responses."   // the Mac's default
            : custom;
        try
        {
            var answer = await ApiService.Shared.VoiceChatAsync(question, instructions, job.Cancellation.Token);
            if (job.Canceled) return null;
            if (answer.Length == 0)
            {
                Status = "No answer";
                _output.Toast(ToastKind.Info, "The AI didn't answer. Try again.", 4);
                return null;
            }
            Status = "Chat completion complete";
            return answer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Voice chat failed: {ex.Kind} {ex.StatusCode} {ex.ServerMessage ?? ex.Message}");
            if (job.Canceled) return null;
            if (ex.StatusCode == 429)
            {
                Status = "Daily limit reached";
                _output.QuotaExceeded(ex.ServerMessage ?? "Daily limit reached", null, null, $"{ApiConstants.WebBaseUrl}/coworker/pricing");
                return null;
            }
            Status = "AI answer failed";
            _output.Toast(ToastKind.Error, ex.Kind == ApiErrorKind.Network ? "Network error. Try again." : "Couldn't get an answer. Try again.", 4);
            return null;
        }
    }

    private void ShowFailure(TranscriptionError error)
    {
        switch (error.Kind)
        {
            case TranscriptionFailure.SignInRequired:
                Status = "Login required";
                _output.SignInRequired(
                    error.Api?.UpgradeUrl != null && error.Message.Length > 0
                        ? error.Message
                        : $"Please connect your Windows app to your web account at {ApiConstants.CoworkerConnectUrl}",
                    error.Api?.UpgradeUrl ?? ApiConstants.CoworkerConnectUrl);
                break;

            case TranscriptionFailure.QuotaExceeded:
                Status = "Daily limit reached";
                _output.QuotaExceeded(error.Message, error.Api?.CurrentUsage, error.Api?.Limit,
                    error.Api?.UpgradeUrl ?? $"{ApiConstants.WebBaseUrl}/coworker/pricing");
                break;

            case TranscriptionFailure.ServerMessage:
                Status = error.Message;
                _output.Toast(ToastKind.Error, error.Message, error.Message.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? 5 : 3);
                break;

            default:
                Status = error.Api?.Kind == ApiErrorKind.Network ? "Network error" : "Transcription failed";
                _output.Toast(ToastKind.Error, error.Message, 4);
                break;
        }
    }

    // ===== Paste =====

    private async Task PasteAsync(string text, IPasteDestination? destination, Job? job, TranscriptionJob? queuedJob = null)
    {
        var source = job?.Transcription ?? queuedJob!;
        var result = await _paster.PasteAsync(text, destination);

        if (result.Outcome == PasteOutcome.Deferred)
        {
            // The shortcut was pressed again while this text was on its way in: it waits for that take,
            // ahead of anything held later, instead of being left on the clipboard.
            DebugLog.Write($"A take started mid-paste: holding {text.Length} characters until it ends");
            _queuedPastes.Insert(0, (text, destination ?? _paster.CaptureDestination(), source));
            DrainQueuedPastes();   // the take may already have ended
            return;
        }

        if (job?.ResponseReceivedAt is { } received)
        {
            DictationTelemetry.SendPaste(source, (DateTime.UtcNow - received).TotalMilliseconds, result.TargetApp, text.Length, result.Method, result.AutoSubmitted);
        }

        switch (result.Outcome)
        {
            case PasteOutcome.ClipboardOnly:
                _output.Toast(ToastKind.Info, "Text copied. Focus a text field and press Ctrl+V to paste.", 4);
                break;
            case PasteOutcome.ElevatedTarget:
                _output.Toast(ToastKind.Info, "Text copied. Coworker can't paste into apps running as administrator; press Ctrl+V.", 4);
                break;
            case PasteOutcome.Unconfirmed:
                _output.Toast(ToastKind.Info, $"Couldn't paste into {result.TargetApp ?? "the app"}. Your text is on the clipboard; press Ctrl+V.", 4);
                break;
            default:
                _output.PlayStopSound();   // the earcon marks the moment the words land, not the key release
                break;
        }
    }

    private void DrainQueuedPastes()
    {
        if (_queuedPastes.Count == 0 || IsCaptureActive) return;

        var queued = _queuedPastes.ToList();
        _queuedPastes.Clear();
        DebugLog.Write($"Draining {queued.Count} held paste(s)");
        _ = PasteQueuedAsync(queued);
    }

    private async Task PasteQueuedAsync(List<(string Text, IPasteDestination? Destination, TranscriptionJob Job)> queued)
    {
        foreach (var (text, destination, job) in queued)
        {
            if (IsCaptureActive)
            {
                // A new take started meanwhile: what is left waits again, in order.
                _queuedPastes.Add((text, destination, job));
                continue;
            }
            await PasteAsync(text, destination, null, job);
        }
    }

    // ===== Cancel =====

    /// Escape: drop the take being recorded and any transcription in flight. Nothing is pasted, nothing plays.
    public void Cancel()
    {
        var wasBusy = IsCancelable;

        if (_take is { } take)
        {
            _audio.LevelChanged -= OnLevel;
            _audio.Frame -= OnFrame;
            _audio.Abort();
            take.Ended = true;
            take.Stream?.Cancel("client_cancel");
            _take = null;
            _stream = null;
            _level = 0;
        }

        foreach (var job in _jobs.Where(j => j.Phase == DictationState.Transcribing).ToList()) CancelJob(job);

        if (wasBusy) Status = "Recording canceled";
        Refresh();
    }

    private void CancelJob(Job job)
    {
        job.Canceled = true;
        DictationTelemetry.Send("coworker.client.request.canceled", "response", "canceled", job.Transcription, job.Transcription.Context,
            durationMs: (DateTime.UtcNow - job.Transcription.StartedAt).TotalMilliseconds, error: "client_cancel");
        job.Cancellation.Cancel();
        job.Transcription.Stream?.Cancel("client_cancel");
        _jobs.Remove(job);
    }

    // ===== Internals =====

    private void EndTake(Take take, bool discardStream, string reason)
    {
        _audio.LevelChanged -= OnLevel;
        _audio.Frame -= OnFrame;
        take.Ended = true;
        if (discardStream) take.Stream?.Cancel(reason);
        if (_take == take) _take = null;
        _stream = null;
        _level = 0;
        Refresh();
        DrainQueuedPastes();
    }

    private void FinishJob(Job job)
    {
        _jobs.Remove(job);
        job.Cancellation.Dispose();
        Refresh();
    }

    private void OnLevel(float level) => _level = level;

    private void OnFrame(ReadOnlyMemory<byte> pcm) => _stream?.Feed(pcm);

    private void Refresh()
    {
        Mode = _take?.Mode ?? _jobs.LastOrDefault(j => j.Phase == DictationState.Transcribing)?.Mode ?? _jobs.LastOrDefault()?.Mode ?? DictationMode.Dictation;
        State = _take is { } take
            ? take.MicrophoneOpen ? DictationState.Recording : DictationState.Starting
            : _jobs.Any(j => j.Phase == DictationState.Transcribing) ? DictationState.Transcribing
            : _jobs.Count > 0 ? DictationState.Pasting
            : DictationState.Idle;
    }

    // The server builds the take's prompt from the translation target, the custom prompt and the active app.
    private static TranscriptionContext BuildContext(AppSettings settings, DictationMode mode)
    {
        var activeApp = Paste.ForegroundApp.Current()?.DisplayName ?? "Unknown";
        return new TranscriptionContext(
            RequestId: Guid.NewGuid().ToString().ToUpperInvariant(),
            DeviceId: SessionService.Shared.DeviceId,
            Model: settings.SelectedModel,
            Language: settings.SelectedLanguage,
            TranslationEnabled: settings.TranslationEnabled,
            TranslationLanguage: settings.SelectedTranslationLanguage,
            CustomPrompt: settings.CustomPrompt ?? "",
            ActiveApp: activeApp,
            TextRules: mode == DictationMode.Dictation,
            Dictionary: DictionaryCache.Snapshot());
    }

    private sealed class Take
    {
        public Take(TranscriptionContext context, ITranscriptionStream? stream, DictationMode mode)
        {
            Context = context;
            Stream = stream;
            Mode = mode;
        }

        public TranscriptionContext Context { get; }
        public DictationMode Mode { get; }
        public ITranscriptionStream? Stream { get; }
        public bool MicrophoneOpen { get; set; }
        public bool Stopping { get; set; }
        public bool Ended { get; set; }
    }

    private sealed class Job
    {
        public Job(TranscriptionJob transcription, DictationMode mode)
        {
            Transcription = transcription;
            Mode = mode;
        }

        public TranscriptionJob Transcription { get; }
        public DictationMode Mode { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public DictationState Phase { get; set; } = DictationState.Transcribing;
        public bool Canceled { get; set; }
        public DateTime? ResponseReceivedAt { get; set; }
    }
}
