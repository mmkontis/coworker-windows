using System.Collections.Concurrent;
using System.Diagnostics;
using Coworker.Helpers;

namespace Coworker.Paste;

/// The app that owns a window: <see cref="DisplayName"/> is what the Mac calls the localized name
/// ("Google Chrome", "Slack") and is what Auto-Enter and the active_app field use.
public sealed record AppIdentity(IntPtr Window, int ProcessId, string ProcessName, string ExePath, string DisplayName)
{
    public bool IsCoworker => ProcessId == Environment.ProcessId;
}

/// Finds which app a window belongs to. UWP and WinUI windows live in an "ApplicationFrameHost" frame; the
/// app that really owns them is found through the frame's child window.
public static class ForegroundApp
{
    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// The app of the window that has the keyboard focus right now.
    public static AppIdentity? Current() => Describe(NativeMethods.GetForegroundWindow());

    public static AppIdentity? Describe(IntPtr window)
    {
        var top = window == IntPtr.Zero ? IntPtr.Zero : ProcessNative.GetAncestor(window, ProcessNative.GA_ROOT);
        if (top == IntPtr.Zero) return null;

        var pid = ProcessNative.ProcessIdOf(top);
        var exe = ProcessNative.ExePathOf(pid);
        if (exe != null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
            ProcessNative.HostedWindow(top) is var hosted && hosted != IntPtr.Zero)
        {
            pid = ProcessNative.ProcessIdOf(hosted);
            exe = ProcessNative.ExePathOf(pid) ?? exe;
        }
        if (pid == 0) return null;

        var processName = exe != null ? Path.GetFileNameWithoutExtension(exe) : ProcessNameOf(pid);
        return new AppIdentity(top, pid, processName, exe ?? "", exe != null ? DisplayNameOf(exe) : processName);
    }

    /// The name a user knows the program by: its file description, else its product name, else the file name.
    /// Anything that lists apps by name (the Auto-Enter lists, clipboard sources) should use this so names match.
    public static string DisplayNameOf(string exePath) =>
        Names.GetOrAdd(exePath, static path =>
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                foreach (var candidate in new[] { info.FileDescription, info.ProductName })
                {
                    if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException) { }
            return Path.GetFileNameWithoutExtension(path);
        });

    private static string ProcessNameOf(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }
}
