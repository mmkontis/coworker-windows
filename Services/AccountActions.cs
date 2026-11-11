namespace Coworker.Services;

/// Account operations that span the session and the shell.
public static class AccountActions
{
    /// The Mac's signOut: disconnects the device, clears the account's embedded workflow browser data,
    /// then the local session (Mac AppView.signOut / completeLocalSignOut).
    public static async Task SignOutAsync()
    {
        var email = SessionService.Shared.UserEmail;
        await SessionService.Shared.SignOutAsync();
        await WorkflowBrowser.ClearSessionAsync(email);
    }
}
