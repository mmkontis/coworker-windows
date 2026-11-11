using Windows.Graphics;

namespace Coworker.Helpers;

/// The work area (screen minus taskbar, in physical pixels, origin included) and DPI scale of a monitor.
public readonly record struct DisplayInfo(RectInt32 WorkArea, double Scale)
{
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    public static DisplayInfo Primary => FromMonitor(NativeMethods.MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));

    public static DisplayInfo FromCursor()
    {
        NativeMethods.GetCursorPos(out var pt);
        return FromMonitor(NativeMethods.MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
    }

    public static DisplayInfo FromWindow(IntPtr hwnd) =>
        FromMonitor(NativeMethods.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    private static DisplayInfo FromMonitor(IntPtr monitor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        var work = new RectInt32(info.rcWork.Left, info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);

        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        return new DisplayInfo(work, scale);
    }
}
