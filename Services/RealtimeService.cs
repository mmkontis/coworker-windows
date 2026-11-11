using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Coworker.Helpers;

namespace Coworker.Services;

/// RequestId names the take the server is answering (the request_id sent with its "stop"); null from
/// servers that predate it.
public sealed record RealtimeTranscription(string Text, JsonElement Data, string? RequestId = null);

public sealed record RealtimeError(string Message, string? RequestId = null);

public sealed record RealtimePartial(string Text, JsonElement Data);

/// A "Send desktop notification" workflow action pushed by the server.
public sealed record RealtimeNotification(string Title, string Body, string? Url);

/// An "Ask a human" prompt. ResponseType is approve_reject, pick_option, free_text or edit_draft.
public sealed record RealtimeHitlPrompt(string Token, string ResponseType, string Question,
    IReadOnlyList<string> Options, string? Draft, string RespondUrl, string? WorkflowName);

/// <summary>
/// Client for the coworker realtime socket (Mac: WebSocketService.swift): wss with the session's
/// Bearer token, reconnect with 1s-30s backoff, dispatch of the server's non-chat messages as typed
/// events on the UI thread. It only connects while a session exists. Chat messages are ignored.
/// </summary>
public sealed class RealtimeService
{
    public static RealtimeService Shared { get; } = new();

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(30);   // two missed pongs
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    // Server messages whose text means the token was refused (Mac isAuthenticationErrorMessage).
    private static readonly string[] AuthErrorMarkers =
        { "authentication required", "missing bearer token", "missing authentication token", "invalid token", "token expired" };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private ClientWebSocket? _socket;
    private TaskCompletionSource _retryNow = new();
    private bool _started;

    private RealtimeService() { }

    /// The socket is open.
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// The server has sent its application-level "connected" message: safe to send "start".
    public bool IsServerReady { get; private set; }

    /// Events are raised on the UI thread.
    public event Action? ServerReady;
    public event Action? Disconnected;
    public event Action? RecordingStarted;
    public event Action? RecordingCanceled;
    public event Action<RealtimeTranscription>? Transcription;
    public event Action<RealtimePartial>? Partial;
    public event Action<RealtimeError>? ServerError;
    public event Action<RealtimeNotification>? Notification;
    public event Action<RealtimeHitlPrompt>? HitlPrompt;

    /// Follows the session from here on: connects when signed in, disconnects when not. Call once.
    public void Start()
    {
        if (_started) return;
        _started = true;

        SessionService.Shared.SessionChanged += _ => SyncWithSession();
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) RetryNow();
        };
        SyncWithSession();
    }

    /// Drops the socket and stops reconnecting.
    public void Disconnect()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _loopCts;
            _loopCts = null;
        }
        cts?.Cancel();
        _socket?.Abort();
    }

    /// Serializes and sends a JSON text message. False when the socket is not open.
    public async Task<bool> SendJsonAsync(object message, CancellationToken ct = default) =>
        await SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ApiClient.Json)), WebSocketMessageType.Text, ct);

    /// Sends a binary frame (audio). False when the socket is not open.
    public Task<bool> SendAudioAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        SendAsync(data, WebSocketMessageType.Binary, ct);

    private async Task<bool> SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type, CancellationToken ct)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return false;

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, type, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            DebugLog.Write($"Realtime send failed: {ex.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SyncWithSession()
    {
        if (SessionService.Shared.IsAuthenticated) Connect();
        else Disconnect();
    }

    private void Connect()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_loopCts != null) return;
            cts = _loopCts = new CancellationTokenSource();
        }
        _ = Task.Run(() => RunAsync(cts));
    }

    private void RetryNow()
    {
        lock (_gate) _retryNow.TrySetResult();
    }

    private async Task RunAsync(CancellationTokenSource loop)
    {
        var ct = loop.Token;
        var delay = InitialDelay;
        while (!ct.IsCancellationRequested)
        {
            var token = SessionService.Shared.IsAuthenticated ? SessionService.Shared.AccessToken : null;
            if (string.IsNullOrEmpty(token)) break;

            var connected = false;
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            socket.Options.KeepAliveInterval = KeepAliveInterval;
            socket.Options.KeepAliveTimeout = KeepAliveTimeout;
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(new Uri(ApiConstants.WebSocketUrl), connectCts.Token).ConfigureAwait(false);
                }

                connected = true;
                _socket = socket;
                delay = InitialDelay;
                DebugLog.Write("Realtime connected");
                await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Connection refused, dropped, timed out or the server closed abnormally: back off and retry.
                DebugLog.Write($"Realtime connection ended: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
                IsServerReady = false;
                if (connected) UiThread.Post(() => Disconnected?.Invoke());
            }

            if (ct.IsCancellationRequested) break;

            Task nudge;
            lock (_gate)
            {
                _retryNow = new TaskCompletionSource();
                nudge = _retryNow.Task;
            }
            try
            {
                await Task.WhenAny(Task.Delay(delay, ct), nudge).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
        }

        lock (_gate)
        {
            // Let the next Connect() start a fresh loop.
            if (ReferenceEquals(_loopCts, loop)) _loopCts = null;
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try
            {
                Dispatch(text);
            }
            catch (JsonException ex)
            {
                DebugLog.Write($"Realtime: unreadable message ({ex.Message})");
            }
        }
    }

    private void Dispatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Str(root, "type") is not { } type) return;

        var text = Str(root, "text");
        var requestId = Str(root, "request_id");
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? d.Clone()
            : default;

        switch (type)
        {
            case "connected":
                IsServerReady = true;
                UiThread.Post(() => ServerReady?.Invoke());
                break;
            case "recording_started":
                UiThread.Post(() => RecordingStarted?.Invoke());
                break;
            case "recording_canceled":
                UiThread.Post(() => RecordingCanceled?.Invoke());
                break;
            case "transcription":
                // An empty text is an answer too: silence, or the server skipping a take it got too
                // little audio for (data.skipped). Dropping it left the take waiting out its timeout.
                UiThread.Post(() => Transcription?.Invoke(new RealtimeTranscription(text ?? "", data, requestId)));
                break;
            case "partial" when text != null:
                UiThread.Post(() => Partial?.Invoke(new RealtimePartial(text, data)));
                break;
            case "error":
                var error = Str(root, "message") ?? "Unknown server error";
                if (AuthErrorMarkers.Any(m => error.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    _ = RecoverFromAuthErrorAsync();
                UiThread.Post(() => ServerError?.Invoke(new RealtimeError(error, requestId)));
                break;
            case "notification":
                UiThread.Post(() => Notification?.Invoke(new RealtimeNotification(
                    Str(data, "title") ?? "Coworker", Str(data, "body") ?? "", Str(data, "url"))));
                break;
            case "hitl_prompt":
                if (ParseHitl(data) is { } prompt) UiThread.Post(() => HitlPrompt?.Invoke(prompt));
                break;
            default:
                // chat_response and anything newer: not part of this app.
                DebugLog.Write($"Realtime: ignoring message type '{type}'");
                break;
        }
    }

    private static RealtimeHitlPrompt? ParseHitl(JsonElement data)
    {
        if (Str(data, "token") is not { } token) return null;

        var options = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : new List<string>();

        return new RealtimeHitlPrompt(
            token,
            Str(data, "response_type") ?? "approve_reject",
            Str(data, "question") ?? "Please review and respond.",
            options,
            Str(data, "draft"),
            Str(data, "respond_url") ?? $"{ApiConstants.WebBaseUrl}/api/coworker/workflows/runs/resume",
            Str(data, "workflow_name"));
    }

    /// The server refused our token: refresh it, and reconnect with the new one.
    private async Task RecoverFromAuthErrorAsync()
    {
        if (await SessionService.Shared.RefreshAsync().ConfigureAwait(false) == SessionRefreshResult.Refreshed)
        {
            _socket?.Abort();
            RetryNow();
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString()
            : null;
}
