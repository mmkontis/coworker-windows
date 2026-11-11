using Coworker.Helpers;
using Coworker.Services;

namespace Coworker.Transcription;

/// <summary>
/// Transcription on the Coworker server (Mac: the WebSocket and HTTP paths of ContentViewModel). A take
/// streams over the realtime socket while it is recorded; if that fails or times out, it is sent once
/// over HTTP from the finished recording. HTTP alone is used whenever
/// the socket never worked.
/// </summary>
internal sealed class ServerTranscriptionEngine : ITranscriptionEngine
{
    public bool Accepts(TranscriptionContext context) => true;

    public ITranscriptionStream? OpenStream(TranscriptionContext context) =>
        WebSocketDictationStream.Open(RealtimeService.Shared, context);

    public async Task<TranscriptionOutcome> TranscribeAsync(TranscriptionJob job, CancellationToken ct)
    {
        if (job.Stream is WebSocketDictationStream stream)
        {
            job.Transport = "websocket";
            var result = await stream.FinishAsync(job.Audio, job.Context.RequestId, job.TimeoutBudgetMs, ct);

            switch (result.Kind)
            {
                case WebSocketDictationStream.FinishKind.Text:
                    return TranscriptionOutcome.Success(result.Text ?? "");
                case WebSocketDictationStream.FinishKind.Terminal:
                    stream.Detach();
                    return TranscriptionOutcome.Fail(TranscriptionFailure.ServerMessage, result.Message ?? "");
                case WebSocketDictationStream.FinishKind.Canceled:
                    return TranscriptionOutcome.Fail(TranscriptionFailure.Canceled, "");
                case WebSocketDictationStream.FinishKind.Retry:
                    job.RetryCount = 1;
                    job.RetryTrigger = result.Trigger;
                    DebugLog.Write($"Sending the take over HTTP after {result.Trigger}");
                    DictationTelemetry.Send("coworker.client.retry.http_fallback", "retry", "retrying", job, job.Context,
                        durationMs: (DateTime.UtcNow - job.StartedAt).TotalMilliseconds, transport: "websocket",
                        metadata: new Dictionary<string, object> { ["trigger"] = result.Trigger ?? "", ["next_transport"] = "http" });
                    // Only stop listening: a "cancel" on the shared socket would also wipe a newer take being
                    // recorded on it. A late answer for this take is ignored by its request id.
                    stream.Detach();
                    break;
                default:
                    stream.Cancel("disconnect_http_fallback");   // release what the server holds for a session that no longer works
                    break;
            }
        }

        job.Transport = "http";
        return await TranscribeOverHttpAsync(job, ct);
    }

    private static async Task<TranscriptionOutcome> TranscribeOverHttpAsync(TranscriptionJob job, CancellationToken ct)
    {
        try
        {
            var result = await ApiService.Shared.TranscribeAsync(job, ct);
            return TranscriptionOutcome.Success(result.Text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TranscriptionOutcome.Fail(TranscriptionFailure.Canceled, "");
        }
        catch (ApiException ex) when (ex.Kind == ApiErrorKind.Unauthorized)
        {
            return TranscriptionOutcome.Fail(TranscriptionFailure.SignInRequired, ex.ServerMessage ?? ex.Message, ex);
        }
        catch (ApiException ex) when (ex.Kind == ApiErrorKind.RateLimited)
        {
            return TranscriptionOutcome.Fail(TranscriptionFailure.QuotaExceeded, ex.Message, ex);
        }
        catch (ApiException ex)
        {
            return TranscriptionOutcome.Fail(TranscriptionFailure.Failed, ex.Message, ex);
        }
    }
}
