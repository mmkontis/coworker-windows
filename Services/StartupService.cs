using Coworker.Helpers;
using Coworker.Models;
using Microsoft.Win32;

namespace Coworker.Services;

/// <summary>
/// Launch at sign-in (Mac: SMAppService.mainApp, setting autoStartEnabled, on by default). An
/// unpackaged app registers a Run-key value; Task Manager's Startup tab can switch it off through
/// StartupApproved, which is honoured. Autostart launches pass --background so the app starts in the tray.
/// </summary>
public static class StartupService
{
    public const string BackgroundArgument = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Coworker";

    /// True when Windows will start the app at sign-in: registered and not switched off in Task Manager.
    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) == null) return false;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    /// Registers or removes the launch entry and remembers the choice. The Settings toggle calls this.
    public static void SetEnabled(bool enabled)
    {
        AppSettings.Instance.AutoStartEnabled = enabled;
        AppSettings.Instance.Save();
        if (IsIsolatedInstance()) return;

        try
        {
            if (enabled) Register();
            else Unregister();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DebugLog.Write($"Could not change launch at login ({ex.Message})");
        }
    }

    /// Brings the registration in line with the setting at launch. Unlike the Mac, which re-enables
    /// itself on every launch, an opt-out stays off. A registration that points at an exe that still
    /// exists is left alone, so a dev build does not steal the installed app's entry.
    public static void Apply()
    {
        if (IsIsolatedInstance() || !AppSettings.Instance.AutoStartEnabled) return;

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is string command && ExecutableOf(command) is { } exe && File.Exists(exe)) return;
            Register();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DebugLog.Write($"Could not register launch at login ({ex.Message})");
        }
    }

    private static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, $"\"{exe}\" {BackgroundArgument}");

        // A stale "disabled" mark from Task Manager would keep the fresh registration off.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static void Unregister()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // A private data folder means a dev or test instance: it must never register itself for sign-in.
    private static bool IsIsolatedInstance() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COWORKER_DATA_DIR"));

    private static string? ExecutableOf(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return null;
        if (command[0] != '"') return command.Split(' ')[0];

        var end = command.IndexOf('"', 1);
        return end > 1 ? command[1..end] : null;
    }
}
