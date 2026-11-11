using Microsoft.Win32;

namespace Coworker.Services;

/// <summary>
/// Windows' microphone privacy switches (Mac: AVCaptureDevice authorization). A blocked desktop app
/// still opens the microphone; it just receives silence, so the switches are read from the consent
/// store: the global one, "Let desktop apps access your microphone", this app's own entry, and the
/// group policy that forces microphone access off.
/// </summary>
public static class MicrophoneConsent
{
    private const string ConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    private const int PolicyForceDeny = 2;   // LetAppsAccessMicrophone

    /// False when Windows will hand the app silence instead of microphone audio.
    public static bool IsAllowed()
    {
        var appKey = (Environment.ProcessPath ?? "").Replace('\\', '#');
        return !IsForbiddenByPolicy() &&
               !IsDenied(Registry.LocalMachine, ConsentPath) &&
               !IsDenied(Registry.CurrentUser, ConsentPath) &&
               !IsDenied(Registry.CurrentUser, ConsentPath + @"\NonPackaged") &&
               (appKey.Length == 0 || !IsDenied(Registry.CurrentUser, ConsentPath + @"\NonPackaged\" + appKey));
    }

    /// Opens Settings, Privacy &amp; security, Microphone.
    public static void OpenSettings() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

    private static bool IsForbiddenByPolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyPath);
            return key?.GetValue("LetAppsAccessMicrophone") is PolicyForceDeny;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsDenied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
