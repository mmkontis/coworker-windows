namespace Coworker.Transcription;

/// Which server errors are worth another attempt (Mac: ContentViewModel.isTerminalServerError and
/// shouldIgnoreStaleWebSocketError).
public static class ServerErrors
{
    private static readonly string[] TerminalMarkers =
    {
        "chat failed",
        "api key",
        "authentication required",
        "quota",
        "maximum supported length",
        "no audio received",
        "sign in to use",
    };

    /// A backend failure that repeating the request would only hit again; it ends the take with a message.
    public static bool IsTerminal(string message) =>
        TerminalMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// A cancelled session's error can land after its replacement HTTP upload is already running.
    /// Only the first WebSocket attempt's error is acted on.
    public static bool ShouldIgnoreStaleWebSocketError(string transport, int retryCount) =>
        !(transport == "websocket" && retryCount == 0);
}
