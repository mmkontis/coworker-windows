using Coworker.Views;
using Microsoft.Win32;

namespace Coworker.Services;

public enum MicrophoneAccess
{
    Allowed,
    /// A Windows privacy switch turns the microphone off for desktop apps.
    Blocked,
    NoDevice,
}

/// <summary>
/// Windows has no per-app microphone prompt for desktop apps: access is two privacy switches in
/// Settings ("Microphone access" and "Let desktop apps access your microphone"). This reads them and
/// shows the Mac's "Microphone Permission Required" window (MicrophonePermissionView) when one is off.
/// </summary>
public static class MicrophonePermission
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static MicrophonePermissionWindow? _window;

    public static MicrophoneAccess Check()
    {
        if (IsDenied(ConsentStore) || IsDenied(ConsentStore + @"\NonPackaged")) return MicrophoneAccess.Blocked;
        return AudioService.IsMicrophoneAvailable ? MicrophoneAccess.Allowed : MicrophoneAccess.NoDevice;
    }

    public static void OpenSettings() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

    /// Shows the permission window (or raises it when already open). UI thread.
    public static void ShowPermissionRequired()
    {
        if (_window != null)
        {
            _window.Activate();
            return;
        }

        _window = new MicrophonePermissionWindow();
        _window.Closed += (_, _) => _window = null;
        _window.Activate();
    }

    /// The microphone could not be opened. A blocked microphone gets the permission window, anything
    /// else (no device, driver trouble) the message.
    public static void Report(string message)
    {
        if (Check() == MicrophoneAccess.Blocked)
        {
            ShowPermissionRequired();
            return;
        }

        ToastService.Show(ToastKind.Error, "Microphone Not Found", message, 8);
    }

    private static bool IsDenied(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
    }
}
