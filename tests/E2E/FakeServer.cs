using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coworker.E2E;

/// <summary>
/// A stand-in for api.humanlike.co with what dictation needs: who the user is, the model list, the dictionary,
/// telemetry, and the live dictation socket (`start`, the audio as binary frames, `stop`) plus the HTTP upload
/// fallback. It keeps everything the app sends for each take, so the test can check the recording, and answers
/// with the transcript the test chose. Anything else gets a 404 and is logged.
/// </summary>
internal sealed class FakeServer : IDisposable
{
    public const string Email = "e2e@humanlike.co";
    public const string UserId = "e2e-user";
    public const string DeviceId = "coworker_E2E";

    private readonly HttpListener _listener = new();
    private readonly string _modelsJson;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Take> _takes = new();
    private readonly List<string> _log = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeServer(int port, string modelsJson)
    {
        BaseUrl = $"http://localhost:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _modelsJson = modelsJson;
    }

    public string BaseUrl { get; }

    /// The transcript the next `stop` (or upload) answers with.
    public string NextTranscript { get; set; } = "";

    /// Completes when the app opens its live dictation socket with a bearer token: it is signed in and ready.
    public Task Connected => _connected.Task;

    public IReadOnlyList<Take> Takes { get { lock (_takes) return _takes.ToList(); } }

    public IReadOnlyList<string> Log { get { lock (_log) return _log.ToList(); } }

    public sealed class Take
    {
        public required string Transport { get; init; }
        public JsonObject? Start { get; init; }
        public JsonObject? Stop { get; set; }
        public MemoryStream Audio { get; } = new();
        public bool Stopped { get; set; }
        public string? Answer { get; set; }
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }

    private void Write(string line)
    {
        var stamped = $"{DateTime.UtcNow:HH:mm:ss.fff} {line}";
        lock (_log) _log.Add(stamped);
        Console.WriteLine($"  [server] {stamped}");
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath;
        // GETs may come through the website's proxy (COWORKER_WEB_BASE + /api/coworker/...).
        if (path.StartsWith("/api/coworker", StringComparison.Ordinal)) path = path["/api".Length..];
        Write($"{request.HttpMethod} {path}{(request.IsWebSocketRequest ? " (websocket)" : "")}");
        try
        {
            if (request.IsWebSocketRequest && path == "/coworker")
            {
                await Socket(context);
                return;
            }
            switch (request.HttpMethod, path)
            {
                case ("GET", "/coworker/user/me"):
                    await Json(context, 200, new { id = UserId, email = Email, device_id = DeviceId, tested_typing_wpm = 40, first_name = "End", last_name = "To End" });
                    break;
                case ("GET", "/coworker/models"):
                    await Raw(context, 200, _modelsJson);
                    break;
                case ("GET", "/coworker/words"):
                    await Json(context, 200, new
                    {
                        success = true,
                        words = new[] { new { id = "w1", word = "Univation", definition = "Univation", shortcut = (string?)null } },
                        count = 1,
                    });
                    break;
                case ("POST", "/coworker/telemetry"):
                    await Drain(request);
                    await Json(context, 200, new { success = true });
                    break;
                case ("POST", "/coworker/transcribe"):
                    await HttpTranscribe(context);
                    break;
                default:
                    await Drain(request);
                    await Raw(context, 404, "{\"success\":false,\"error\":\"Not part of the end-to-end fake server\"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Write($"handler error on {path}: {ex.GetType().Name}: {ex.Message}");
            try { context.Response.Abort(); } catch (Exception) { }
        }
    }

    private async Task Socket(HttpListenerContext context)
    {
        var hasBearer = (context.Request.Headers["Authorization"] ?? "").StartsWith("Bearer ", StringComparison.Ordinal);
        var socket = (await context.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
        Write($"websocket open, bearer token: {(hasBearer ? "yes" : "NO")}");
        await Send(socket, new { type = "connected", message = "Connected to the end-to-end fake server" });
        if (hasBearer) _connected.TrySetResult();

        Take? take = null;
        var buffer = new byte[64 * 1024];
        var text = new MemoryStream();
        while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                Write($"websocket closed: {ex.Message}");
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Write("websocket closed by the app");
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch (Exception) { }
                return;
            }
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                take?.Audio.Write(buffer, 0, result.Count);
                continue;
            }

            text.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = JsonNode.Parse(Encoding.UTF8.GetString(text.ToArray())) as JsonObject;
            text.SetLength(0);
            var type = json?["type"]?.GetValue<string>();
            switch (type)
            {
                case "start":
                    take = new Take { Transport = "websocket", Start = json };
                    lock (_takes) _takes.Add(take);
                    Write($"start: {json!.ToJsonString()}");
                    await Send(socket, new { type = "recording_started", message = "Recording started. Send audio as binary frames." });
                    break;
                case "stop":
                    var requestId = json?["request_id"]?.GetValue<string>();
                    if (take == null)
                    {
                        await Send(socket, new { type = "error", message = "stop without start", request_id = requestId });
                        break;
                    }
                    take.Stopped = true;
                    take.Stop = json;
                    take.Answer = NextTranscript;
                    Write($"stop: {take.Audio.Length} audio bytes, answering \"{take.Answer}\"" +
                          (json?["field"] is { } stopField ? $", field {stopField.ToJsonString()}" : ""));
                    await Task.Delay(250);   // a transcription takes a moment
                    await Send(socket, new
                    {
                        type = "transcription",
                        text = take.Answer,
                        request_id = requestId,
                        data = new { usage_remaining = 1000, is_premium = true, model_used = take.Start?["model"]?.GetValue<string>() },
                    });
                    break;
                case "cancel":
                    Write($"cancel: {json?.ToJsonString()}");
                    await Send(socket, new { type = "recording_canceled", message = "Recording canceled" });
                    break;
                default:
                    Write($"websocket message: {json?.ToJsonString()}");
                    break;
            }
        }
    }

    private async Task HttpTranscribe(HttpListenerContext context)
    {
        var take = new Take { Transport = "http" };
        await context.Request.InputStream.CopyToAsync(take.Audio);
        take.Stopped = true;
        take.Answer = NextTranscript;
        lock (_takes) _takes.Add(take);
        Write($"HTTP upload: {take.Audio.Length} bytes (multipart), answering \"{take.Answer}\"");
        await Json(context, 200, new { text = take.Answer, is_premium = true });
    }

    private static async Task Send(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static Task Json(HttpListenerContext context, int status, object body) =>
        Raw(context, status, JsonSerializer.Serialize(body));

    private static async Task Raw(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task Drain(HttpListenerRequest request)
    {
        if (request.HasEntityBody) await request.InputStream.CopyToAsync(Stream.Null);
    }
}
