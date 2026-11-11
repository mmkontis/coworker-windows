using System.Text.Json;
using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Services;

namespace Coworker.Transcription;

/// <summary>
/// One take streamed over the realtime socket (Mac: the WebSocket half of ContentViewModel and
/// WebSocketService): audio flows to the server while the user speaks, so the answer is ready almost as
/// soon as the shortcut is released. Frames wait in a bounded buffer until the server acknowledges the
/// start, and the oldest are dropped first when it fills. Whatever goes wrong, the take can still be
/// sent over HTTP, because the finished recording is kept. Handlers run on the UI thread.
/// </summary>
internal sealed class WebSocketDictationStream : ITranscriptionStream
{
    private const int IdleCapBytes = 30 * 16_000 * 2;          // ~30 s until the start is acknowledged
    private const int ActiveCapBytes = 5 * 60 * 16_000 * 2;    // ~5 min while streaming, so a network blip loses nothing
    private static readonly TimeSpan ServerReadyWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartAckWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FieldWait = TimeSpan.FromMilliseconds(250);   // longest the start waits for the field
    private const string ChatModel = "gpt-5.6-luna";   // sent as the Mac does; dictation never uses it

    public enum FinishKind
    {
        Text,

        /// Send the take again over HTTP; <see cref="FinishResult.Trigger"/> says why.
        Retry,

        /// The stream never worked (dropped, or the server never acknowledged): plain HTTP, not a retry.
        Unavailable,

        /// The server refused the take; <see cref="FinishResult.Message"/> is for the user.
        Terminal,

        Canceled,
    }

    public readonly record struct FinishResult(FinishKind Kind, string? Text = null, string? Message = null, string? Trigger = null);

    private readonly RealtimeService _realtime;
    private readonly TranscriptionContext _context;
    private readonly object _bufferLock = new();
    private readonly Queue<byte[]> _buffer = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _drainStop = new();

    private int _bufferBytes;
    private volatile bool _streaming;
    private volatile bool _disconnected;
    private volatile bool _capturing = true;
    private volatile int _sending;
    private bool _startSent;
    private volatile bool _fieldSent;
    private bool _detached;
    private bool _canceled;
    private double _budgetMs;
    private string? _requestId;
    private TaskCompletionSource<FinishResult>? _pending;
    private CancellationTokenSource? _timeout;

    private WebSocketDictationStream(RealtimeService realtime, TranscriptionContext context)
    {
        _realtime = realtime;
        _context = context;
    }

    public event Action<string>? Failed;

    /// The socket is open, the server said it is ready and audio is flowing.
    public bool IsStreaming => _streaming && !_disconnected;

    /// The socket dropped while the take was being recorded.
    public bool HadDisconnect => _disconnected;

    /// Starts a session on the shared realtime socket, or returns null when no session is signed in.
    public static WebSocketDictationStream? Open(RealtimeService realtime, TranscriptionContext context)
    {
        if (!SessionService.Shared.IsAuthenticated) return null;

        var stream = new WebSocketDictationStream(realtime, context);
        stream.Attach();
        return stream;
    }

    public void Feed(ReadOnlyMemory<byte> pcm)
    {
        if (pcm.IsEmpty || _detached) return;

        lock (_bufferLock)
        {
            var cap = _capturing && _streaming ? ActiveCapBytes : IdleCapBytes;
            // Recency matters more than completeness: the finished recording still has everything.
            while (_bufferBytes + pcm.Length > cap && _buffer.Count > 0)
                _bufferBytes -= _buffer.Dequeue().Length;
            _buffer.Enqueue(pcm.ToArray());
            _bufferBytes += pcm.Length;
        }
        _wake.Release();
    }

    /// Waits for the server's transcription of the take that just ended.
    public async Task<FinishResult> FinishAsync(CapturedAudio audio, string requestId, double budgetMs, CancellationToken ct)
    {
        _capturing = false;
        if (!IsStreaming) return new FinishResult(FinishKind.Unavailable);

        // The tail of the take may still be queued; the server needs all of it before "stop".
        var flushDeadline = Environment.TickCount64 + (long)FlushWait.TotalMilliseconds;
        while ((_bufferBytes > 0 || _sending > 0) && IsStreaming && Environment.TickCount64 < flushDeadline)
            await Task.Delay(10, CancellationToken.None);
        if (!IsStreaming) return new FinishResult(FinishKind.Unavailable);

        _budgetMs = budgetMs;
        _requestId = requestId;
        var pending = _pending = new TaskCompletionSource<FinishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = _timeout = new CancellationTokenSource();
        using var timeoutRegistration = timeout.Token.Register(() => pending.TrySetResult(new FinishResult(FinishKind.Retry, Trigger: "adaptive_timeout")));
        using var cancelRegistration = ct.Register(() => Cancel("client_cancel"));
        timeout.CancelAfter(TimeSpan.FromMilliseconds(budgetMs));

        var stop = new Dictionary<string, object>
        {
            ["type"] = "stop",
            ["chat_mode"] = false,
            ["request_id"] = requestId,
            ["client_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["recorded_duration_ms"] = (int)audio.Duration.TotalMilliseconds,
            ["response_timeout_ms"] = (int)budgetMs,
        };
        // The server also takes the field with the stop, for a read that finished after the start went out.
        if (!_fieldSent && _context.Field is { } field) stop["field"] = field;
        var sent = await _realtime.SendJsonAsync(stop, CancellationToken.None);
        if (!sent) pending.TrySetResult(new FinishResult(FinishKind.Retry, Trigger: "disconnect_during_recording"));

        var result = await pending.Task;
        timeout.Dispose();
        _timeout = null;
        _pending = null;
        if (result.Kind == FinishKind.Text) Detach();
        return result;
    }

    public void Cancel(string reason)
    {
        if (_canceled) return;
        _canceled = true;

        _pending?.TrySetResult(new FinishResult(FinishKind.Canceled));
        if (_startSent) _ = _realtime.SendJsonAsync(new Dictionary<string, object> { ["type"] = "cancel", ["reason"] = reason });
        Detach();
    }

    /// Stops listening; safe to call more than once.
    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        _realtime.ServerReady -= OnServerReady;
        _realtime.RecordingStarted -= OnRecordingStarted;
        _realtime.Transcription -= OnTranscription;
        _realtime.Partial -= OnPartial;
        _realtime.ServerError -= OnServerError;
        _realtime.Disconnected -= OnDisconnected;

        _streaming = false;
        _drainStop.Cancel();
        lock (_bufferLock)
        {
            _buffer.Clear();
            _bufferBytes = 0;
        }
    }

    private void Attach()
    {
        _realtime.ServerReady += OnServerReady;
        _realtime.RecordingStarted += OnRecordingStarted;
        _realtime.Transcription += OnTranscription;
        _realtime.Partial += OnPartial;
        _realtime.ServerError += OnServerError;
        _realtime.Disconnected += OnDisconnected;

        _ = DrainAsync(_drainStop.Token);

        if (_realtime.IsServerReady)
        {
            SendStart();
            return;
        }

        // Not connected yet: the tap keeps recording, and the start goes out as soon as the server is ready.
        _ = Task.Delay(ServerReadyWait).ContinueWith(_ => UiThread.Post(() =>
        {
            if (!_startSent && !_detached) DebugLog.Write("Realtime server not ready in time; the take will go over HTTP");
        }));
    }

    private void SendStart()
    {
        if (_startSent || _detached) return;
        _startSent = true;

        var message = new Dictionary<string, object>
        {
            ["type"] = "start",
            ["device_id"] = _context.DeviceId,
            ["model"] = _context.Model,
            ["language"] = _context.Language,
            ["chat_mode"] = false,
            ["chat_model"] = ChatModel,
            ["sample_rate"] = 16_000,
            ["audio_format"] = "int16",
            ["active_app"] = _context.ActiveApp,
            ["enable_functions"] = false,
            ["text_rules"] = _context.TextRules,
        };
        // No "prompt": the server builds it from these (Mac: buildEffectivePrompt).
        if (_context.TranslateTo is { } translateTo) message["translate_to"] = translateTo;
        if (_context.CustomPrompt.Length > 0) message["custom_prompt"] = _context.CustomPrompt;
        if (_context.Dictionary.Count > 0) message["dictionary"] = _context.Dictionary;

        _ = SendStartAsync(message);
    }

    private async Task SendStartAsync(Dictionary<string, object> message)
    {
        // The field the take will be typed into is still being read over UI Automation. Waiting for it a moment
        // costs nothing (audio is buffered until the start is acknowledged); a slower read goes with the stop.
        if (_context.FieldRead is { IsCompleted: false } fieldRead)
        {
            await Task.WhenAny(fieldRead, Task.Delay(FieldWait));
            if (_detached || _canceled) return;   // the take ended meanwhile: a start now would never be stopped
        }
        if (_context.Field is { } field)
        {
            message["field"] = field;
            _fieldSent = true;
        }

        if (!await _realtime.SendJsonAsync(message)) _disconnected = true;

        // No acknowledgement: the audio sits in the buffer and the finished recording goes over HTTP.
        await Task.Delay(StartAckWait);
        if (_streaming || _detached) return;
        UiThread.Post(() =>
        {
            if (_streaming || _detached) return;
            DebugLog.Write("Realtime start was not acknowledged; the take will go over HTTP");
            DictationTelemetry.Send("coworker.client.websocket.start_timeout", "websocket", "error", null, _context,
                durationMs: StartAckWait.TotalMilliseconds, transport: "websocket", error: "recording_start_timeout");
        });
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false);
                while (_streaming && !ct.IsCancellationRequested && TryDequeue(out var frame))
                {
                    _sending = 1;
                    try
                    {
                        if (!await _realtime.SendAudioAsync(frame, ct).ConfigureAwait(false)) break;
                    }
                    finally
                    {
                        _sending = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Detached: nothing more to send.
        }
    }

    private bool TryDequeue(out byte[] frame)
    {
        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
            {
                frame = Array.Empty<byte>();
                return false;
            }
            frame = _buffer.Dequeue();
            _bufferBytes -= frame.Length;
            return true;
        }
    }

    private void OnServerReady() => SendStart();

    private void OnRecordingStarted()
    {
        _streaming = true;
        _wake.Release();
    }

    /// Every take listens on the shared socket, and a stacked take can still be waiting for its answer
    /// while the next one records: a reply naming another take's request is not ours. Untagged replies
    /// (older servers) are taken as before.
    private bool IsForAnotherTake(string? requestId) => requestId != null && requestId != _requestId;

    private void OnTranscription(RealtimeTranscription transcription)
    {
        if (IsForAnotherTake(transcription.RequestId)) return;

        // The server skipped the take (too little audio reached it over the socket): the full recording
        // goes over HTTP right away instead of after the adaptive timeout.
        if (transcription.Text.Length == 0 && transcription.Data.ValueKind == JsonValueKind.Object &&
            transcription.Data.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True)
        {
            DebugLog.Write("Server skipped the streamed take; sending it over HTTP now");
            _pending?.TrySetResult(new FinishResult(FinishKind.Retry, Trigger: "server_skipped"));
            return;
        }
        _pending?.TrySetResult(new FinishResult(FinishKind.Text, Text: transcription.Text));
    }

    private void OnPartial(RealtimePartial partial) =>
        _timeout?.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(10_000, _budgetMs)));

    private void OnServerError(RealtimeError error)
    {
        if (IsForAnotherTake(error.RequestId)) return;

        var message = error.Message;
        DictationTelemetry.Send("coworker.client.websocket.error", "websocket", "error", null, _context,
            transport: "websocket", error: message);

        if (_pending == null)
        {
            // Refused while the take was still being recorded (Mac: onError with no pending request).
            Failed?.Invoke(message);
            return;
        }

        _pending.TrySetResult(ServerErrors.IsTerminal(message)
            ? new FinishResult(FinishKind.Terminal, Message: message)
            : new FinishResult(FinishKind.Retry, Trigger: "websocket_error"));
    }

    private void OnDisconnected()
    {
        _disconnected = true;
        _streaming = false;

        if (_pending != null)
        {
            // The answer will never arrive on a socket that just died.
            _pending.TrySetResult(new FinishResult(FinishKind.Retry, Trigger: "disconnect_during_recording"));
            return;
        }

        DictationTelemetry.Send("coworker.client.websocket.disconnect_during_capture", "websocket", "degraded", null, _context,
            transport: "websocket", error: "socket_dropped_capture_continues");
    }
}
