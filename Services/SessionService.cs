using CommunityToolkit.Mvvm.Messaging;
using Coworker.Controls;
using Coworker.Helpers;
using Coworker.Models;
using Microsoft.Win32;

namespace Coworker.Services;

public enum SessionRefreshResult
{
    Refreshed,
    /// The server refused the token; the session was invalidated.
    Rejected,
    /// Network/server trouble; the session is kept and the caller may try again later.
    Failed,
}

public enum SessionChange
{
    SignedIn,
    Refreshed,
    SignedOut,
    Invalidated,
}

/// <summary>
/// The signed-in session: connect-token sign-in, launch verification, 30-minute token refresh,
/// invalidation on 401, and sign-out. Mac: CoworkerSessionManager + the session code in
/// APIService/AppView. The token itself lives in <see cref="CredentialStore"/> (via AppSettings.AccessToken).
/// </summary>
public sealed class SessionService
{
    public static SessionService Shared { get; } = new();

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SignOutServerWait = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Task<SessionRefreshResult>? _refreshTask;
    private CancellationTokenSource? _loopCts;

    private SessionService() { }

    /// Raised on the UI thread after every state change.
    public event Action<SessionChange>? SessionChanged;

    /// Raised on the UI thread when the session was dropped without the user asking (with the reason).
    public event Action<string>? SessionInvalidated;

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(AppSettings.Instance.UserEmail) &&
        !string.IsNullOrEmpty(AppSettings.Instance.AccessToken) &&
        AppSettings.Instance.IsConnected;

    public string? UserEmail => AppSettings.Instance.UserEmail;
    public string? UserId => AppSettings.Instance.ConnectedUserId;
    public string? AccessToken => AppSettings.Instance.AccessToken;

    /// The company the user is connected under (coworker_users.company_id).
    public string? CompanyId => AppSettings.Instance.CompanyId;

    /// Stable per-machine id (`coworker_<MachineGuid>`), rotated on sign-out. An id already on disk,
    /// or carried in the token, wins so existing sessions keep working.
    public string DeviceId
    {
        get
        {
            lock (_gate)
            {
                var settings = AppSettings.Instance;
                if (!string.IsNullOrEmpty(settings.DeviceId)) return settings.DeviceId;

                var token = settings.AccessToken;
                var fromToken = string.IsNullOrEmpty(token) ? null : JwtClaims.Get(token, "device_id");
                settings.DeviceId = !string.IsNullOrEmpty(fromToken) ? fromToken : $"coworker_{MachineGuid() ?? Guid.NewGuid().ToString().ToUpperInvariant()}";
                settings.Save();
                return settings.DeviceId;
            }
        }
    }

    /// Exchanges the deep-link connect token for a session and returns the connected email.
    /// Throws <see cref="ApiException"/> when the server refuses or answers with no session.
    public async Task<string> SignInWithConnectTokenAsync(string connectToken, string? emailFromLink)
    {
        var result = await ApiService.Shared.ConnectDevice(connectToken, emailFromLink).ConfigureAwait(false)
                     ?? throw ApiException.InvalidResponse();

        StoreSession(result, emailFromLink);
        Raise(SessionChange.SignedIn);
        _ = TranscriptionModelCatalog.Shared.RefreshAsync();   // hydrate the model list after login (Mac: refreshModelCatalog)
        return AppSettings.Instance.UserEmail ?? "";
    }

    /// Launch check (Mac verifyExistingConnection): confirms the stored session with the server.
    /// A 401/403 ends in <see cref="Invalidate"/> (inside the API pipeline); anything else keeps the session.
    public async Task VerifyAsync()
    {
        if (!IsAuthenticated) return;
        try
        {
            var me = await ApiService.Shared.GetUserMe().ConfigureAwait(false);
            var settings = AppSettings.Instance;
            if (!string.IsNullOrEmpty(me.Id)) settings.ConnectedUserId = me.Id;
            if (!string.IsNullOrEmpty(me.Email)) settings.UserEmail = me.Email;
            if (me.TestedTypingWpm is > 0) settings.TestedTypingWpm = me.TestedTypingWpm;
            settings.UserFullName = me.FullName;
            settings.UserPhotoUrl = me.PhotoUrl;
            settings.Save();
            Raise(SessionChange.Refreshed);   // the sidebar picks up the name and photo
            _ = TranscriptionModelCatalog.Shared.RefreshAsync();
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Session verify: {ex.Kind} {ex.StatusCode}");
        }
    }

    /// Re-registers the device for a fresh token (Mac reconnectDevice). Concurrent callers share one request.
    public async Task<SessionRefreshResult> RefreshAsync()
    {
        Task<SessionRefreshResult> task;
        lock (_gate) task = _refreshTask ??= Task.Run(RefreshCoreAsync);
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_refreshTask, task)) _refreshTask = null;
            }
        }
    }

    private async Task<SessionRefreshResult> RefreshCoreAsync()
    {
        if (!IsAuthenticated) return SessionRefreshResult.Failed;
        try
        {
            var result = await ApiService.Shared.ReconnectDevice().ConfigureAwait(false);
            if (result == null) return SessionRefreshResult.Failed;

            StoreSession(result, null);
            Raise(SessionChange.Refreshed);
            return SessionRefreshResult.Refreshed;
        }
        catch (ApiException ex) when (ex.Kind is ApiErrorKind.Unauthorized or ApiErrorKind.Forbidden)
        {
            Invalidate($"Token refresh was rejected ({ex.StatusCode})");
            return SessionRefreshResult.Rejected;
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Session refresh failed: {ex.Kind} {ex.StatusCode}");
            return SessionRefreshResult.Failed;
        }
    }

    /// Starts the launch sequence (verify, refresh) and the 30-minute refresh timer. Call once.
    public void Start()
    {
        _loopCts?.Cancel();
        var cts = _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(cts.Token));
    }

    public void Stop() => _loopCts?.Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await VerifyAsync().ConfigureAwait(false);
            if (IsAuthenticated) await RefreshAsync().ConfigureAwait(false);

            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (IsAuthenticated) await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// User-initiated sign-out: tells the server (best effort), clears the session and rotates the device id.
    public async Task SignOutAsync()
    {
        if (IsAuthenticated)
        {
            await Task.WhenAny(TryDisconnectAsync(), Task.Delay(SignOutServerWait)).ConfigureAwait(false);
        }

        ClearSession();
        var settings = AppSettings.Instance;
        settings.DeviceId = $"coworker_{Guid.NewGuid().ToString().ToUpperInvariant()}";
        settings.Save();
        Raise(SessionChange.SignedOut);
    }

    private static async Task TryDisconnectAsync()
    {
        try { await ApiService.Shared.DisconnectDevice().ConfigureAwait(false); }
        catch (ApiException) { }
    }

    /// Drops the session because the server no longer accepts it.
    public void Invalidate(string reason)
    {
        bool hadSession;
        lock (_gate)
        {
            hadSession = IsAuthenticated || !string.IsNullOrEmpty(AppSettings.Instance.AccessToken);
            if (hadSession) ClearSession();
        }
        if (!hadSession) return;

        DebugLog.Write($"Session invalidated: {reason}");
        UiThread.Post(() =>
        {
            SessionInvalidated?.Invoke(reason);
            WeakReferenceMessenger.Default.Send(new SessionInvalidatedMessage(reason));
        });
        Raise(SessionChange.Invalidated);
    }

    private void StoreSession(ApiService.ConnectDeviceResult result, string? fallbackEmail)
    {
        var settings = AppSettings.Instance;
        settings.AccessToken = result.AccessToken;
        // Never blank an id we already have (a refresh may omit it).
        if (!string.IsNullOrEmpty(result.UserId)) settings.ConnectedUserId = result.UserId;
        var email = !string.IsNullOrEmpty(result.Email) ? result.Email : fallbackEmail;
        if (!string.IsNullOrEmpty(email)) settings.UserEmail = email;
        if (result.CompanyId != null) settings.CompanyId = result.CompanyId;
        if (result.IsAcademic != null) settings.IsAcademic = result.IsAcademic.Value;
        settings.IsConnected = true;
        settings.Save();
    }

    private static void ClearSession()
    {
        var settings = AppSettings.Instance;
        settings.AccessToken = null;
        settings.UserEmail = null;
        settings.ConnectedUserId = null;
        settings.UserFullName = null;
        settings.UserPhotoUrl = null;
        settings.IsConnected = false;
        settings.CompanyId = null;
        settings.SelectedCompanyId = "all";
        settings.IsAcademic = false;
        settings.Save();
    }

    private void Raise(SessionChange change) => UiThread.Post(() => SessionChanged?.Invoke(change));

    private static string? MachineGuid()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return (key?.GetValue("MachineGuid") as string)?.ToUpperInvariant();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
