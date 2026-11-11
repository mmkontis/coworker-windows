using System.Text.Json;

namespace Coworker.Services;

public enum ApiErrorKind
{
    /// 401, or no signed-in session at all (see <see cref="ApiException.SignedOut"/>).
    Unauthorized,
    Forbidden,
    /// 429: daily quota / rate limit. Carries usage, limit and upgrade URL when the server sends them.
    RateLimited,
    /// No connection, DNS/TLS failure or timeout.
    Network,
    /// 5xx.
    Server,
    /// Any other non-2xx status.
    Http,
    /// 2xx with a body that could not be understood.
    InvalidResponse,
}

/// <summary>
/// Every failure from <see cref="ApiClient"/>. <see cref="Exception.Message"/> is the Mac's
/// user-facing copy (APIError in APIService.swift); use <see cref="ApiErrorMessages"/> for the
/// per-screen variants.
/// </summary>
public sealed class ApiException : Exception
{
    public const string DefaultQuotaMessage = "Daily limit reached. Upgrade to premium for unlimited transcriptions!";

    public ApiErrorKind Kind { get; }
    public int? StatusCode { get; }

    /// True when the call was refused locally because nobody is signed in (Mac: authenticationRequired).
    public bool SignedOut { get; init; }

    /// Message from the response body (detail.message / message / error), if any.
    public string? ServerMessage { get; init; }

    public int? CurrentUsage { get; init; }
    public int? Limit { get; init; }
    public string? UpgradeUrl { get; init; }

    public ApiException(ApiErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public static ApiException NotSignedIn() =>
        new(ApiErrorKind.Unauthorized, "Authentication required. Please sign in to continue.") { SignedOut = true };

    public static ApiException Network(Exception inner, bool timedOut = false) =>
        new(ApiErrorKind.Network, timedOut ? "Network error: The request timed out." : $"Network error: {inner.Message}", null, inner);

    public static ApiException InvalidResponse(Exception? inner = null) =>
        new(ApiErrorKind.InvalidResponse, "Failed to decode response", null, inner);

    /// Builds the typed error for a non-success HTTP status and its (possibly empty) JSON body.
    public static ApiException FromStatus(int status, string? body)
    {
        var parsed = ErrorBody.Parse(body);
        var kind = status switch
        {
            401 => ApiErrorKind.Unauthorized,
            403 => ApiErrorKind.Forbidden,
            429 => ApiErrorKind.RateLimited,
            >= 500 => ApiErrorKind.Server,
            _ => ApiErrorKind.Http,
        };
        var message = kind switch
        {
            ApiErrorKind.Unauthorized => "Session expired. Please reconnect your account",
            ApiErrorKind.Forbidden => "Access denied. Please check your permissions",
            ApiErrorKind.RateLimited => parsed.Message ?? DefaultQuotaMessage,
            ApiErrorKind.Server => $"Server error ({status})",
            _ => $"HTTP error: {status}",
        };
        return new ApiException(kind, message, status)
        {
            ServerMessage = parsed.Message,
            CurrentUsage = parsed.CurrentUsage,
            Limit = parsed.Limit,
            UpgradeUrl = parsed.UpgradeUrl,
        };
    }

    private sealed record ErrorBody(string? Message, int? CurrentUsage, int? Limit, string? UpgradeUrl)
    {
        // The node answers with {detail:{message,current_usage,limit,upgrade_url}}, {detail:"..."},
        // or the same fields at the top level ({error|message: "..."}).
        public static ErrorBody Parse(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new(null, null, null, null);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new(null, null, null, null);

                var scope = root;
                string? message = null;
                if (root.TryGetProperty("detail", out var detail))
                {
                    if (detail.ValueKind == JsonValueKind.String) message = detail.GetString();
                    else if (detail.ValueKind == JsonValueKind.Object) scope = detail;
                }

                message ??= ReadString(scope, "message") ?? ReadString(scope, "error") ?? ReadString(root, "message") ?? ReadString(root, "error");
                return new(message, ReadInt(scope, "current_usage"), ReadInt(scope, "limit"), ReadString(scope, "upgrade_url"));
            }
            catch (JsonException)
            {
                return new(null, null, null, null);
            }
        }

        private static string? ReadString(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;

        private static int? ReadInt(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }
}

public enum ApiContext
{
    Default,
    Dictionary,
    History,
    Analytics,
}

/// Per-screen user-facing copy, verbatim from the Mac (AppView.swift, HistoryPageView.swift,
/// AnalyticsPageView.swift).
public static class ApiErrorMessages
{
    public static string For(Exception ex, ApiContext context)
    {
        if (ex is not ApiException api) return ex.Message;

        switch (context)
        {
            case ApiContext.Dictionary:
                if (api.Kind == ApiErrorKind.Unauthorized)
                {
                    var hasEmail = !string.IsNullOrEmpty(SessionService.Shared.UserEmail);
                    return api.SignedOut && hasEmail
                        ? "Security update: Please Sign Out and Reconnect to sync dictionary."
                        : "Session expired. Please reconnect your account";
                }
                return api.Kind == ApiErrorKind.Forbidden
                    ? "Access denied. Please check your permissions"
                    : "Failed to load dictionary. Try again from the Dictionary tab.";

            case ApiContext.History:
                if (api.Kind == ApiErrorKind.Unauthorized)
                    return api.SignedOut
                        ? "Please sign in to view your transcription history."
                        : "Session expired. Please sign in again.";
                return api.Kind == ApiErrorKind.Forbidden
                    ? "Access denied. Please reconnect your account."
                    : api.Message;

            case ApiContext.Analytics:
                if (api.Kind == ApiErrorKind.Unauthorized)
                    return api.SignedOut
                        ? "Please sign in again to load your synced analytics."
                        : "Your session expired. Please reconnect your account.";
                return api.Kind == ApiErrorKind.Forbidden
                    ? "Access denied. Please reconnect your account and try again."
                    : api.Message;

            default:
                return api.Message;
        }
    }
}
