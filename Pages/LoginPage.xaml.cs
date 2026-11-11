using Coworker.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Coworker.Pages;

public sealed partial class LoginPage : UserControl
{
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(2) };

    public LoginPage()
    {
        InitializeComponent();
        // The Mac copy also promises "AI chat", which the Windows app does not have.
        BodyText.Text = "Connect your account to unlock transcription\nand cloud sync.";
        ElementShadow.Attach(ButtonShadow, 10, Color.FromArgb(0x26, 0, 0, 0), 12, 3);
    }

    // No source parameter: the web app then redirects to coworker-auth://callback, back into this app.
    private async void OnGoogleSignIn(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(await ResolveLoginUrlAsync());

    private void OnLearnMore(object sender, RoutedEventArgs e) =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ApiConstants.CoworkerLearnMoreUrl));

    // Mac resolveLoginURL: a local dev web app that is not running falls back to production.
    private static async Task<Uri> ResolveLoginUrlAsync()
    {
        var login = new Uri(ApiConstants.CoworkerLoginUrl);
        if (login.Host != "localhost") return login;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, login);
            using var response = await Probe.SendAsync(request);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 400 or 405) return login;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            DebugLog.Write($"Local login unavailable, using production ({ex.Message})");
        }
        return new Uri($"{ApiConstants.ProductionWebBaseUrl}/coworker/login");
    }
}
