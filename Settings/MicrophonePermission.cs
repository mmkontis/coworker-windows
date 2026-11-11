using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace Coworker.Settings;

public enum MicrophoneAccess
{
    Granted,
    /// The user (or their organisation's privacy setting for this user) switched microphone access off.
    Denied,
    /// Blocked at the machine level: the device-wide switch or a group policy.
    Restricted,
    NotGranted,
}

/// Windows has no per-app microphone prompt for desktop apps: access is the global "Microphone
/// access" switch plus "Let desktop apps access your microphone" (Settings > Privacy & security >
/// Microphone), or a policy. This reads them the way Windows stores them.
public static class MicrophonePermission
{
    public const string PrivacySettingsUri = "ms-settings:privacy-microphone";

    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";

    public static MicrophoneAccess Read()
    {
        // LetAppsAccessMicrophone: 1 = force allow, 2 = force deny.
        if (ReadPolicy() == 2) return MicrophoneAccess.Restricted;
        if (IsDenied(Registry.LocalMachine, ConsentStore)) return MicrophoneAccess.Restricted;
        if (IsDenied(Registry.CurrentUser, ConsentStore) || IsDenied(Registry.CurrentUser, ConsentStore + @"\NonPackaged"))
            return MicrophoneAccess.Denied;

        try
        {
            return DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture).CurrentStatus switch
            {
                DeviceAccessStatus.DeniedBySystem => MicrophoneAccess.Restricted,
                DeviceAccessStatus.DeniedByUser => MicrophoneAccess.Denied,
                DeviceAccessStatus.Unspecified => MicrophoneAccess.NotGranted,
                _ => MicrophoneAccess.Granted,
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return MicrophoneAccess.Granted;
        }
    }

    /// Raised (on a background thread) when the user changes microphone access in Windows Settings.
    /// Dispose the returned object to stop listening.
    public static IDisposable Watch(Action changed)
    {
        var info = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture);
        void OnAccessChanged(DeviceAccessInformation sender, DeviceAccessChangedEventArgs args) => changed();
        info.AccessChanged += OnAccessChanged;
        return new Subscription(() => info.AccessChanged -= OnAccessChanged);
    }

    private static bool IsDenied(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static int? ReadPolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue("LetAppsAccessMicrophone") as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
