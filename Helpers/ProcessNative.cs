using System.Runtime.InteropServices;
using System.Text;

namespace Coworker.Helpers;

/// Win32 imports for finding out which app a window belongs to, whether it runs elevated, and
/// bringing it to the front (the paste engine and the active-app lookup).
internal static class ProcessNative
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;
    public const int SW_RESTORE = 9;
    private const int ERROR_ACCESS_DENIED = 5;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    /// The process id that owns <paramref name="hwnd"/>; 0 when the window is gone.
    public static int ProcessIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        InputNative.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// The full path of the process's executable, or null when it cannot be read.
    public static string? ExePathOf(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            var size = (uint)name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// True when the process runs with administrator rights. A process whose token cannot even be opened
    /// from here is one we could not send keys to either, so it counts as elevated.
    public static bool IsElevated(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static string ClassNameOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    /// The window of the app that really owns an "ApplicationFrameHost" frame (a UWP or WinUI app), or 0.
    public static IntPtr HostedWindow(IntPtr frame)
    {
        var found = IntPtr.Zero;
        var frameOwner = ProcessIdOf(frame);
        EnumChildWindows(frame, (child, _) =>
        {
            if (ProcessIdOf(child) != frameOwner && ClassNameOf(child) == "Windows.UI.Core.CoreWindow")
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// Brings a window to the front, even when Windows' foreground lock would refuse a plain call.
    public static bool BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : InputNative.GetWindowThreadProcessId(foreground, out _);
        var thisThread = InputNative.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            NativeMethods.SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            if (NativeMethods.GetForegroundWindow() != hwnd) SwitchToThisWindow(hwnd, true);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
        return NativeMethods.GetForegroundWindow() == hwnd;
    }
}
