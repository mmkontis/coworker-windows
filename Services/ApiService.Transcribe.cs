using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coworker.Helpers;
using Coworker.Transcription;

namespace Coworker.Services;

public partial class ApiService
{
    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TelemetryTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RuntimeConfigTimeout = TimeSpan.FromSeconds(8);

    /// POST /coworker/transcribe (multipart). Throws <see cref="ApiException"/>: RateLimited on the daily
    /// quota (429, carries usage/limit/upgrade URL), Unauthorized when the session is gone, Http with the
    /// server's own message when the answer holds an error instead of text.
    public async Task<TranscribeResult> TranscribeAsync(TranscriptionJob job, CancellationToken ct)
    {
        var context = job.Context;
        var options = new ApiRequestOptions
        {
            Timeout = TranscribeTimeout,
            Headers = new Dictionary<string, string> { ["X-Coworker-Request-Id"] = context.RequestId },
        };

        DebugLog.Write($"transcribe POST wav={job.Audio.Wav.Length}B model={context.Model} lang={context.HttpLanguage} retry={job.RetryCount}");
        try
        {
            var json = await _api.SendAsync(HttpMethod.Post, "/coworker/transcribe", () => BuildTranscribeForm(job), options, ct)
                .ConfigureAwait(false);
            DebugLog.Write($"transcribe OK bodyLen={json.Length}");   // never log the body: it is the dictated text

            var response = JsonSerializer.Deserialize<TranscribeResponse>(json, JsonOpts);
            if (response?.Text != null) return new TranscribeResult(response.Text, response.IsPremium);

            var message = response?.Detail ?? response?.Error?.Message;
            throw message != null
                ? new ApiException(ApiErrorKind.Http, message)
                : new ApiException(ApiErrorKind.InvalidResponse, "Failed to parse response");
        }
        catch (JsonException ex)
        {
            throw ApiException.InvalidResponse(ex);
        }
    }

    /// POST /coworker/telemetry: a client event for the server's latency dashboards.
    public async Task SendTelemetryAsync(object payload) =>
        await _api.SendAsync(HttpMethod.Post, "/coworker/telemetry", ApiClient.JsonBody(payload),
            new ApiRequestOptions { Timeout = TelemetryTimeout }).ConfigureAwait(false);

    /// GET /coworker/runtime-config (no auth): the defaults the admin dashboard pushes.
    public Task<RuntimeConfig> GetRuntimeConfig(CancellationToken ct = default) =>
        _api.GetAsync<RuntimeConfig>("/coworker/runtime-config",
            new ApiRequestOptions { Authenticated = false, Timeout = RuntimeConfigTimeout }, ct);

    // Field order follows the Mac's multipart body (CoworkerApp.performTranscription).
    private static MultipartFormDataContent BuildTranscribeForm(TranscriptionJob job)
    {
        var context = job.Context;
        var form = new MultipartFormDataContent();

        void AddField(string name, string value)
        {
            var field = new StringContent(value);
            field.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
            form.Add(field);
        }

        AddField("device_id", context.DeviceId);
        AddField("request_id", context.RequestId);
        AddField("model", context.Model);
        AddField("language", context.HttpLanguage);
        AddField("active_app", context.ActiveApp);
        // No "prompt": the server builds it from translate_to, custom_prompt and active_app.
        if (context.TranslateTo is { } translateTo) AddField("translate_to", translateTo);
        if (context.CustomPrompt.Length > 0) AddField("custom_prompt", context.CustomPrompt);
        if (context.TextRules) AddField("text_rules", "true");
        if (context.DictionaryJson is { } dictionary) AddField("dictionary", dictionary);
        // The focused field as JSON (label, placeholder, id, input type, role; never its value). The HTTP
        // fallback is the same take moments later, into the same field, so it carries the field too.
        if (context.FieldJson is { } field) AddField("field", field);
        if (job.RetryCount > 0)
        {
            AddField("attempt_number", (job.RetryCount + 1).ToString());
            AddField("is_retry", "true");
            if (!string.IsNullOrEmpty(job.RetryTrigger)) AddField("retry_trigger", job.RetryTrigger);
        }

        // Names are quoted explicitly: some hosts reject the unquoted form .NET emits by default.
        var audio = new ByteArrayContent(job.Audio.Wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        audio.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"audio_file\"",
            FileName = "\"audio.wav\"",
        };
        form.Add(audio);
        return form;
    }

    public sealed record TranscribeResult(string Text, bool? IsPremium);

    public sealed record RuntimeConfig(
        [property: JsonPropertyName("audio_quality_preset")] string? AudioQualityPreset,
        [property: JsonPropertyName("default_transcription_model")] string? DefaultTranscriptionModel,
        [property: JsonPropertyName("version")] int Version);

    private sealed record TranscribeResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("is_premium")] bool? IsPremium,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("error")] TranscribeError? Error);

    private sealed record TranscribeError([property: JsonPropertyName("message")] string? Message);
}
