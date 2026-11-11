using Coworker.Helpers;
using Coworker.Views;

namespace Coworker.Services;

/// <summary>
/// Shows the upgrade panel (Mac showUpgradeWindow): the sign-in prompt when nobody is signed in or the
/// server refused the session, and the plan prompt when the daily transcription quota is used up. One
/// panel at a time; a new request replaces the open one. Callable from any thread.
/// </summary>
public static class UpgradeService
{
    private static UpgradeWindow? _window;

    private static string PricingUrl => $"{ApiConstants.WebBaseUrl}/coworker/pricing";

    /// <param name="upgradeUrl">Opened by the action button; "connect" in it makes this the sign-in prompt.</param>
    public static void Show(string message, string? upgradeUrl = null, int? currentUsage = null, int? limit = null) =>
        UiThread.Post(() =>
        {
            _ = _window?.DismissAsync();

            var window = new UpgradeWindow(Localize(message), upgradeUrl ?? PricingUrl, currentUsage, limit, Open);
            window.Closed += (_, _) =>
            {
                if (_window == window) _window = null;
            };
            _window = window;
            window.Show();
        });

    /// Recording or transcribing without a session (Mac: the isUserAuthenticated guard).
    public static void ShowSignInRequired() =>
        Show("Please connect your Windows app to your web account to use transcription features.", ApiConstants.CoworkerConnectUrl);

    /// Shows the panel for a transcription that the server refused: quota used up (429) or session
    /// refused (401). Returns false, showing nothing, for any other failure.
    public static bool TryShow(Exception ex)
    {
        if (ex is not ApiException api) return false;

        switch (api.Kind)
        {
            case ApiErrorKind.RateLimited:
                Show(api.Message, api.UpgradeUrl, api.CurrentUsage, api.Limit);
                return true;

            case ApiErrorKind.Unauthorized:
                if (api.ServerMessage != null && api.UpgradeUrl != null) Show(api.ServerMessage, api.UpgradeUrl);
                else Show($"Please connect your Windows app to your web account at {ApiConstants.CoworkerConnectUrl}", ApiConstants.CoworkerConnectUrl);
                return true;

            default:
                return false;
        }
    }

    private static void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
    }

    // The server's copy is written for the Mac app.
    private static string Localize(string message) => message.Replace("Mac app", "Windows app", StringComparison.Ordinal);
}
