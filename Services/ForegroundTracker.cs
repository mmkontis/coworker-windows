using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Coworker.Helpers;

namespace Coworker.Services;

/// <param name="ProcessName">The exe name without extension: what Auto Enter stores and PasteService matches.</param>
/// <param name="DisplayName">The name shown to the user (the exe's file description).</param>
public sealed record ForegroundApp(IntPtr Hwnd, string ProcessName, string DisplayName, string ExecutablePath);

/// <summary>
/// Remembers the last app the user was in before Coworker's tray (Mac ActiveAutoEnterTarget.capture).
/// Clicking the tray icon makes the taskbar the foreground window, so "the active app" has to be
/// tracked as it changes rather than read on click. Coworker's own windows and the shell are skipped.
/// </summary>
internal static class ForegroundTracker
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "Progman", "WorkerW", "ForegroundStaging", "MultitaskingViewFrame",
    };

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    private static readonly WinEventProc Callback = OnForegroundChanged;   // kept alive for the hook
    private static IntPtr _hook;
    private static ForegroundApp? _last;

    /// The last app in front that was neither Coworker nor the shell, if its window still exists.
    public static ForegroundApp? Last => _last != null && IsWindow(_last.Hwnd) ? _last : null;

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, Callback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        Capture(NativeMethods.GetForegroundWindow());
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) =>
        Capture(hwnd);

    private static void Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString())) return;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return;

        var path = ExecutablePath(pid);
        if (path == null) return;

        var processName = Path.GetFileNameWithoutExtension(path);
        _last = new ForegroundApp(hwnd, processName, DisplayName(path, processName), path);
    }

    private static string? ExecutablePath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string DisplayName(string path, string fallback)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? fallback : description.Trim();
        }
        catch (IOException)
        {
            return fallback;
        }
    }
}
