using System.Runtime.InteropServices;

namespace Coworker.Bar;

/// Win32 imports for a per-pixel-alpha (layered) window: the GDI surface the bar is drawn into,
/// UpdateLayeredWindow, cursors built from bitmaps, and the virtual-desktop manager.
internal static class BarNative
{
    public const uint ULW_ALPHA = 0x2;
    public const byte AC_SRC_OVER = 0;
    public const byte AC_SRC_ALPHA = 1;
    public const uint DIB_RGB_COLORS = 0;
    public const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int Width;
        public int Height;

        public SIZE(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorUsed;
        public uint ColorImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Coworker.Helpers.NativeMethods.POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref Coworker.Helpers.NativeMethods.POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Coworker.Helpers.NativeMethods.POINT pt, uint flags);

    public const uint PM_REMOVE = 0x1;
    private const uint QS_ALLINPUT = 0x04FF;

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out Coworker.Helpers.InputNative.MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[]? handles, bool waitAll, uint milliseconds, uint wakeMask);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// Blocks until the compositor's next frame, so each animation frame lands on a display refresh. When DWM
    /// can't say (a locked session, a remote desktop), waits ~16 ms instead, waking early for new messages.
    public static void WaitForNextFrame()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (DwmFlush() == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) return;
        MsgWaitForMultipleObjects(0, null, false, 16, QS_ALLINPUT);
    }

    /// A top-down 32-bit BGRA surface of the given size, selected into a memory DC. The caller owns all three handles.
    public static (IntPtr Dc, IntPtr Bitmap, IntPtr Bits) CreateSurface(int width, int height)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = 32 },
        };
        var bitmap = CreateDIBSection(screen, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);
        SelectObject(dc, bitmap);
        return (dc, bitmap, bits);
    }

    /// A cursor from 32-bit premultiplied BGRA pixels.
    public static IntPtr CreateCursor(int size, Action<IntPtr> paint, int hotspotX, int hotspotY)
    {
        var (dc, bitmap, bits) = CreateSurface(size, size);
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        try
        {
            paint(bits);
            var info = new ICONINFO { IsIcon = false, HotspotX = hotspotX, HotspotY = hotspotY, Mask = mask, Color = bitmap };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(mask);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    // ===== Virtual desktops =====

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManagerClass { }

    /// Keeps a window on the desktop the user is looking at (Mac: canJoinAllSpaces).
    public sealed class DesktopFollower
    {
        private IVirtualDesktopManager? _manager;

        public DesktopFollower()
        {
            try { _manager = (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
            catch (COMException) { }
        }

        /// Moves <paramref name="window"/> to the virtual desktop of <paramref name="reference"/> when it is not on the current one.
        public void Follow(IntPtr window, IntPtr reference)
        {
            if (_manager == null || reference == IntPtr.Zero) return;
            try
            {
                if (_manager.IsWindowOnCurrentVirtualDesktop(window, out var onCurrent) != 0 || onCurrent) return;
                if (_manager.GetWindowDesktopId(reference, out var desktop) == 0) _manager.MoveWindowToDesktop(window, ref desktop);
            }
            catch (COMException) { }
        }
    }
}
