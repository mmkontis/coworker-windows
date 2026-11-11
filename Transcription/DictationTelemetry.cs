using Coworker.Helpers;
using Coworker.Services;

namespace Coworker.Transcription;

/// Fire-and-forget client events for the server's latency dashboards (Mac: emitClientTelemetry).
/// Timings and routes only; the dictated text is never sent.
public static class DictationTelemetry
{
    public static void Send(string eventName, string step, string status, TranscriptionJob? job, TranscriptionContext context,
        double? durationMs = null, string? transport = null, string? error = null, Dictionary<string, object>? metadata = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["event"] = eventName,
            ["step"] = step,
            ["status"] = status,
            ["device_id"] = context.DeviceId,
            ["request_id"] = context.RequestId,
            ["mode"] = "dictation",
        };

        if (job != null)
        {
            payload["transport"] = transport ?? job.Transport;
            payload["recorded_duration_ms"] = (int)Math.Round(job.Audio.Duration.TotalMilliseconds);
            payload["response_timeout_ms"] = (int)Math.Round(job.TimeoutBudgetMs);
            payload["retry_count"] = job.RetryCount;
        }
        else if (transport != null)
        {
            payload["transport"] = transport;
        }

        if (!string.IsNullOrEmpty(context.Model)) payload["model"] = context.Model;
        if (durationMs is { } duration) payload["duration_ms"] = (int)Math.Round(duration);
        if (!string.IsNullOrEmpty(error)) payload["error_message"] = error;
        if (metadata is { Count: > 0 }) payload["metadata"] = metadata;

        Post(payload);
    }

    /// Reports where and how the text went in.
    public static void SendPaste(TranscriptionJob job, double sinceResponseMs, string? targetApp, int textLength, string method, bool autoSubmit)
    {
        Post(new Dictionary<string, object>
        {
            ["event"] = "coworker.client.paste.triggered",
            ["request_id"] = job.Context.RequestId,
            ["device_id"] = job.Context.DeviceId,
            ["mode"] = "dictation",
            ["transport"] = job.Transport,
            ["step"] = "paste",
            ["status"] = "ok",
            ["duration_ms"] = (int)Math.Round(sinceResponseMs),
            ["recorded_duration_ms"] = (int)Math.Round(job.Audio.Duration.TotalMilliseconds),
            ["response_timeout_ms"] = (int)Math.Round(job.TimeoutBudgetMs),
            ["retry_count"] = job.RetryCount,
            ["model"] = job.Context.Model,
            ["metadata"] = new Dictionary<string, object>
            {
                ["target_app"] = targetApp ?? "unknown",
                ["text_length"] = textLength,
                ["method"] = method,
                ["auto_submit"] = autoSubmit,
                ["accessibility_granted"] = true,   // Windows has no accessibility permission to be missing
            },
        });
    }

    private static void Post(Dictionary<string, object> payload)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ApiService.Shared.SendTelemetryAsync(payload).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                DebugLog.Write($"Telemetry not sent: {ex.Kind}");
            }
        });
    }
}
