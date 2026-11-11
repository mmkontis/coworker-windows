using System.Security.Cryptography;
using System.Text;
using Coworker.Helpers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Coworker.Services;

/// <summary>
/// The embedded workflow editor (Mac WorkflowBrowserStore): the web app's editor in a WebView2 that
/// outlives page changes, so unsaved input survives switching tabs. Every account and server gets its
/// own WebView2 profile, and signing out deletes it. The device token is never put in a URL or a script.
/// </summary>
public sealed class WorkflowBrowser
{
    public static WorkflowBrowser Shared { get; } = new();

    public const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    // Mac: web sign-in lands on the dashboard overview, from where the editor is opened.
    private static readonly string[] DashboardPaths = { "/coworker/dashboard", "/coworker/dashboard/" };

    private string? _identity;
    private string? _origin;

    private WorkflowBrowser() { }

    /// The web view, or null before <see cref="Prepare"/> or after the session was cleared.
    public WebView2? View { get; private set; }

    public bool IsLoading { get; private set; }
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }

    /// Why the editor could not be shown, or null.
    public string? ErrorMessage { get; private set; }

    /// True when the failure is a missing WebView2 runtime, which retrying alone will not fix.
    public bool IsRuntimeMissing { get; private set; }

    /// Raised on the UI thread whenever any of the state above changed.
    public event Action? StateChanged;

    public Uri WorkflowsUrl => new($"{ApiConstants.WebBaseUrl}/coworker/dashboard/workflows");

    public Uri CurrentUrl => Uri.TryCreate(View?.Source?.ToString(), UriKind.Absolute, out var url) ? url : WorkflowsUrl;

    /// Mac prepare(account:): builds the web view for this account and server unless it already exists.
    public async void Prepare(string? email)
    {
        var identity = Identity(email);
        var origin = ApiConstants.WebBaseUrl;
        if (View != null && _identity == identity && _origin == origin) return;

        Discard();
        _identity = identity;
        _origin = origin;
        SetError(null);

        var view = new WebView2();
        View = view;
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.Runtime.InteropServices.COMException)
        {
            IsRuntimeMissing = true;
            SetError("The Microsoft Edge WebView2 Runtime is required to show workflows. Install it, then try again.");
            return;
        }

        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, AppPaths.GetPath("WebView2"), new CoreWebView2EnvironmentOptions());
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = ProfileName(origin, identity);
            options.IsInPrivateModeEnabled = false;
            await view.EnsureCoreWebView2Async(environment, options);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (View == view) SetError(ex.Message);
            return;
        }

        // A newer Prepare() replaced this view while it was starting.
        if (View != view) return;

        await Configure(view.CoreWebView2, origin);
        Navigate(WorkflowsUrl);
    }

    public void ShowWorkflows()
    {
        SetError(null);
        Navigate(WorkflowsUrl);
    }

    public void Reload()
    {
        SetError(null);
        if (View?.CoreWebView2 == null) Prepare(_identity == GuestIdentity ? null : _identity);
        else if (string.IsNullOrEmpty(View.Source?.ToString())) Navigate(WorkflowsUrl);
        else View.Reload();
    }

    public void GoBack() => View?.GoBack();

    public void GoForward() => View?.GoForward();

    /// Mac clearSession(account:): wipes the account's editor data (for the production and the local
    /// dev server) and drops the web view.
    public static async Task ClearSessionAsync(string? email)
    {
        var identity = Identity(email);
        var browser = Shared;

        if (browser.View?.CoreWebView2?.Profile is { } profile)
        {
            try
            {
                await profile.ClearBrowsingDataAsync();
                profile.Delete();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                DebugLog.Write($"Workflow profile could not be cleared ({ex.Message})");
            }
        }
        browser.Discard();

        foreach (var origin in new[] { ApiConstants.ProductionWebBaseUrl, ApiConstants.WebBaseUrl })
            TryDeleteProfileFolder(ProfileName(origin, identity));
    }

    private async Task Configure(CoreWebView2 core, string origin)
    {
        // Hide the dashboard shell around the editor, in the app's own pages only. Canvas panels, node
        // settings and the organizer keep their UI.
        await core.AddScriptToExecuteOnDocumentCreatedAsync($$"""
            (() => {
              if (window !== window.top || location.origin !== '{{origin}}') return;
              const style = document.createElement('style');
              style.textContent = `
                .coworker-workspace > aside { display: none !important; }
                .coworker-workspace > main { width: 100%; min-width: 0; }
              `;
              const install = () => (document.head || document.documentElement).appendChild(style);
              if (document.documentElement) install();
              else document.addEventListener('DOMContentLoaded', install, { once: true });
            })();
            """);

        core.NavigationStarting += (sender, e) =>
        {
            IsLoading = true;
            SetError(null);

            // Mac decidePolicyFor: web pages load here; other schemes (mailto, integration callbacks)
            // open outside, but only when the user clicked a link.
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme is "http" or "https" or "about" or "blob" or "data") return;

            e.Cancel = true;
            IsLoading = false;
            if (e.IsUserInitiated) _ = Windows.System.Launcher.LaunchUriAsync(uri);
        };

        core.NewWindowRequested += (sender, e) =>
        {
            e.Handled = true;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;

            // Mac createWebViewWith: the app's own host loads in place, other sites open in the browser.
            if (uri.Host == new Uri(origin).Host) core.Navigate(uri.ToString());
            else if (uri.Scheme is "http" or "https") _ = Windows.System.Launcher.LaunchUriAsync(uri);
        };

        core.NavigationCompleted += (sender, e) =>
        {
            IsLoading = false;
            if (!e.IsSuccess)
            {
                // A navigation replaced by another one is not an error (Mac ignores NSURLErrorCancelled).
                if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) SetError(Describe(e.WebErrorStatus));
                else Raise();
                return;
            }

            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var url) && url.Host == new Uri(origin).Host && DashboardPaths.Contains(url.AbsolutePath))
                Navigate(WorkflowsUrl);
            Raise();
        };

        core.HistoryChanged += (_, _) =>
        {
            CanGoBack = core.CanGoBack;
            CanGoForward = core.CanGoForward;
            Raise();
        };

        core.ProcessFailed += (_, _) => SetError("The workflow editor stopped responding. Reload to continue.");
    }

    private void Navigate(Uri url)
    {
        if (View?.CoreWebView2 is { } core) core.Navigate(url.ToString());
    }

    private void SetError(string? message)
    {
        ErrorMessage = message;
        if (message == null) IsRuntimeMissing = false;
        Raise();
    }

    private void Raise() => UiThread.Post(() => StateChanged?.Invoke());

    private void Discard()
    {
        var view = View;
        View = null;
        _identity = null;
        _origin = null;
        IsLoading = false;
        CanGoBack = false;
        CanGoForward = false;
        SetError(null);
        // Closing a view that already left the tree (its page was dropped) is harmless.
        view?.Close();
    }

    private static string Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.Timeout => "The request timed out.",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.CertificateIsInvalid => "A secure connection to the server could not be made.",
        _ => "The Internet connection appears to be offline.",
    };

    private const string GuestIdentity = "guest";

    private static string Identity(string? email) =>
        string.IsNullOrWhiteSpace(email) ? GuestIdentity : email.Trim().ToLowerInvariant();

    // A stable profile name per server and account (letters and digits only, as WebView2 requires).
    private static string ProfileName(string origin, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{origin}|{identity}"));
        return "wf" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static void TryDeleteProfileFolder(string profileName)
    {
        try
        {
            var folder = Path.Combine(AppPaths.GetPath("WebView2"), "EBWebView", profileName);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked by the browser process: the profile's data was cleared above.
            DebugLog.Write($"Workflow profile folder not deleted ({ex.Message})");
        }
    }
}
