using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coworker.Helpers;

namespace Coworker.Services;

public sealed class ApiRequestOptions
{
    public static readonly ApiRequestOptions Default = new();

    /// Mac convention for GETs: try the web proxy (humanlike.co/api/coworker/...) first, then the node.
    public static readonly ApiRequestOptions ViaWebProxy = new() { WebProxyFirst = true };

    /// Attach the session's Bearer token; refuse locally when nobody is signed in.
    public bool Authenticated { get; init; } = true;

    public bool WebProxyFirst { get; init; }

    /// On 401/403, refresh the session once and retry. Off for the refresh call itself.
    public bool RefreshOnUnauthorized { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// ResponseHeadersRead for streaming bodies (SSE).
    public HttpCompletionOption Completion { get; init; } = HttpCompletionOption.ResponseContentRead;

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// The single HTTP pipeline for the coworker API. One static HttpClient; the Bearer token is set
/// per request (never on shared default headers); 401/403 triggers one session refresh + retry;
/// failures surface as <see cref="ApiException"/>.
/// Feature code adds endpoints as partial <see cref="ApiService"/> files that call this.
/// </summary>
public sealed class ApiClient
{
    public static ApiClient Shared { get; } = new();

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private ApiClient() { }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        // Timeouts are per call (CancellationTokenSource), not the 100s HttpClient default.
        return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public static Func<HttpContent?> JsonBody(object body)
    {
        var json = JsonSerializer.Serialize(body, Json);
        return () => new StringContent(json, Encoding.UTF8, "application/json");
    }

    public async Task<T> GetAsync<T>(string path, ApiRequestOptions? options = null, CancellationToken ct = default) =>
        Deserialize<T>(await SendAsync(HttpMethod.Get, path, null, options, ct).ConfigureAwait(false));

    public async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, ApiRequestOptions? options = null, CancellationToken ct = default) =>
        Deserialize<T>(await SendAsync(method, path, body == null ? null : JsonBody(body), options, ct).ConfigureAwait(false));

    public async Task<string> SendAsync(HttpMethod method, string path, Func<HttpContent?>? content = null,
        ApiRequestOptions? options = null, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(method, path, content, options, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// Sends the request and returns a 2xx response, or throws <see cref="ApiException"/>. The caller
    /// disposes the response. <paramref name="path"/> is a node path such as "/coworker/words".
    public async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, Func<HttpContent?>? content = null,
        ApiRequestOptions? options = null, CancellationToken ct = default)
    {
        options ??= ApiRequestOptions.Default;

        if (options.Authenticated && !SessionService.Shared.IsAuthenticated)
            throw ApiException.NotSignedIn();

        var candidates = BuildCandidates(method, path, options);
        var refreshed = false;

        while (true)
        {
            ApiException? lastError = null;
            var needsRefresh = false;

            foreach (var (url, isProxy) in candidates)
            {
                try
                {
                    var response = await SendOnceAsync(method, url, content, options, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && (!isProxy || IsJson(response)))
                    {
                        return response;
                    }

                    var status = (int)response.StatusCode;
                    var succeeded = response.IsSuccessStatusCode;
                    var body = succeeded ? null : await ReadBodyAsync(response, ct).ConfigureAwait(false);
                    response.Dispose();

                    // A 2xx that reaches here is a proxy answering with a non-JSON page: fall back to the node.
                    lastError = succeeded ? ApiException.InvalidResponse() : ApiException.FromStatus(status, body);

                    if (!succeeded && options.Authenticated && options.RefreshOnUnauthorized && !refreshed && status is 401 or 403)
                    {
                        needsRefresh = true;
                        break;
                    }
                }
                catch (HttpRequestException ex)
                {
                    lastError = ApiException.Network(ex);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = ApiException.Network(new TimeoutException(), timedOut: true);
                }
            }

            if (needsRefresh)
            {
                refreshed = true;
                var result = await SessionService.Shared.RefreshAsync().ConfigureAwait(false);
                if (result == SessionRefreshResult.Refreshed) continue;
                // Rejected: SessionService already invalidated the session. Failed: transient, keep it.
            }

            throw lastError ?? ApiException.InvalidResponse();
        }
    }

    private static List<(string Url, bool IsProxy)> BuildCandidates(HttpMethod method, string path, ApiRequestOptions options)
    {
        var candidates = new List<(string, bool)>(2);
        if (options.WebProxyFirst && method == HttpMethod.Get && path.StartsWith("/coworker", StringComparison.Ordinal))
            candidates.Add((ApiConstants.WebProxyBaseUrl + path["/coworker".Length..], true));
        candidates.Add((ApiConstants.ApiBaseUrl + path, false));
        return candidates;
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string url, Func<HttpContent?>? content,
        ApiRequestOptions options, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content?.Invoke() };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Don't wait for "100 Continue": some hosts never send it, which stalls multipart POSTs.
        request.Headers.ExpectContinue = false;

        if (options.Authenticated)
        {
            var token = SessionService.Shared.AccessToken;
            if (string.IsNullOrEmpty(token)) throw ApiException.NotSignedIn();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (options.Headers != null)
        {
            foreach (var (name, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        return await Http.SendAsync(request, options.Completion, timeout.Token).ConfigureAwait(false);
    }

    private static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { return null; }
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json) ?? throw ApiException.InvalidResponse();
        }
        catch (JsonException ex)
        {
            throw ApiException.InvalidResponse(ex);
        }
    }
}
