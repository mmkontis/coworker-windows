using Coworker.Helpers;

namespace Coworker.Services;

/// Handles `coworker-auth://callback?connect_token=...&email=...` (and coworker://connect|callback)
/// links from the web sign-in page. Mac: handleDeepLink / handleConnectionCallback in CoworkerApp.swift.
public static class DeepLinkService
{
    /// Signs in from a connection link, then raises the window and reports the outcome. UI thread.
    public static async Task HandleAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsConnectionLink(uri))
        {
            DebugLog.Write($"Deep link ignored: {uri?.Scheme}://{uri?.Host}");
            return;
        }
        DebugLog.Write($"Deep link received: {uri.Scheme}://{uri.Host}");

        // Raise the window now, while Windows still lets us take the foreground; the sign-in request
        // below can take a second, and any click or key press in the meantime revokes that right.
        WindowService.Shared.Show();

        var query = ParseQuery(uri.Query);
        var token = query.GetValueOrDefault("connect_token") ?? query.GetValueOrDefault("token");
        var email = query.GetValueOrDefault("email");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(email))
        {
            DebugLog.Write("Deep link: no connect token or email");
            ToastService.Show(ToastKind.Error, null, "Invalid connection link");
            return;
        }

        try
        {
            var connectedEmail = await SessionService.Shared.SignInWithConnectTokenAsync(token, email);
            DebugLog.Write("Deep link: signed in");
            WindowService.Shared.Show();
            ToastService.Show(ToastKind.Success, null, $"Successfully connected to {connectedEmail}");
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Deep link: sign-in failed {ex.Kind} {ex.StatusCode} {ex.ServerMessage ?? ex.Message}");
            WindowService.Shared.Show();
            ToastService.Show(ToastKind.Error, null, $"Failed to connect: {ex.ServerMessage ?? ex.Message}");
        }
    }

    // Mac: any coworker-auth:// link is a callback; coworker:// only for the connect and callback hosts.
    private static bool IsConnectionLink(Uri uri)
    {
        if (uri.Scheme.Equals(ApiConstants.CoworkerCallbackScheme, StringComparison.OrdinalIgnoreCase)) return true;

        return uri.Scheme.Equals(ApiConstants.CoworkerAlternateScheme, StringComparison.OrdinalIgnoreCase) &&
               (uri.Host.Equals("connect", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("callback", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[System.Net.WebUtility.UrlDecode(pair[..eq])] = System.Net.WebUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return result;
    }
}
