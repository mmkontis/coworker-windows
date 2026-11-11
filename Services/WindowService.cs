using Coworker.Helpers;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Coworker.Services;

/// The main window's chrome: size, position, icon, minimum size, close behaviour, raising it to the
/// front, and a hook point for window messages (hotkeys, tray). Mac: WindowManager.showMainDesktopWindow.
public sealed class WindowService
{
    public static WindowService Shared { get; } = new();

    // Mac contentRect / contentMinSize, in DIP.
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 800;
    private const int MinWidth = 1040;
    private const int MinHeight = 680;

    private Window? _window;
    private AppWindow? _appWindow;
    private WindowSubclass? _subclass;

    private WindowService() { }

    public IntPtr Hwnd { get; private set; }

    /// Sets up chrome for the main window. Call once from its constructor.
    public void Initialize(Window window, UIElement titleBar)
    {
        _window = window;
        Hwnd = WindowNative.GetWindowHandle(window);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Hwnd));

        var display = DisplayInfo.FromWindow(Hwnd);
        var work = display.WorkArea;
        var width = (int)Math.Min(work.Width - 100 * display.Scale, DefaultWidth * display.Scale);
        var height = (int)Math.Min(work.Height - 80 * display.Scale, DefaultHeight * display.Scale);
        _appWindow.Resize(new SizeInt32(width, height));
        _appWindow.Move(new PointInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "coworker.ico"));

        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);

        var preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(Hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

        // Closing hides the window to the tray (no taskbar button, like the Mac's red button) instead
        // of exiting, unless we are quitting or the session is ending (cancelling Closing would
        // otherwise stall logoff and installers).
        _appWindow.Closing += (_, e) =>
        {
            if (App.IsQuitting || App.IsSessionEnding) return;
            e.Cancel = true;
            Hide();
        };

        _subclass = new WindowSubclass(Hwnd);
        _subclass.Add(NativeMethods.WM_GETMINMAXINFO, OnGetMinMaxInfo);
        _subclass.Add(NativeMethods.WM_QUERYENDSESSION, (IntPtr wParam, IntPtr lParam, out IntPtr result) =>
        {
            App.IsSessionEnding = true;
            result = (IntPtr)1;   // agree to end the session
            return true;
        });
        _subclass.Add(NativeMethods.WM_ENDSESSION, (IntPtr wParam, IntPtr lParam, out IntPtr result) =>
        {
            App.IsSessionEnding = wParam != IntPtr.Zero;   // false: another app vetoed the shutdown
            if (App.IsSessionEnding) App.PrepareToQuit();
            result = IntPtr.Zero;
            return true;
        });
    }

    /// Runs <paramref name="hook"/> for a window message sent to the main window (on the UI thread).
    internal void AddMessageHook(uint message, WindowSubclass.MessageHook hook) => _subclass?.Add(message, hook);

    /// Brings the main window to the front: shows it if it was hidden to the tray, restores it if it
    /// was minimized.
    public void Show()
    {
        if (_window == null || _appWindow == null) return;

        if (!_appWindow.IsVisible) _appWindow.Show();
        if (_appWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        _window.Activate();
        if (NativeMethods.SetForegroundWindow(Hwnd) && NativeMethods.GetForegroundWindow() == Hwnd) return;

        // Windows refuses the foreground to an app the user isn't using (a sign-in link arriving from
        // the browser, say). Sharing the input state of the app in front for a moment lifts that.
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var thisThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread &&
                       NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(Hwnd);
            NativeMethods.SetForegroundWindow(Hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
        }
        DebugLog.Write($"Window raise: foreground is main window = {NativeMethods.GetForegroundWindow() == Hwnd}");
    }

    /// Hides the window (and its taskbar button); the app keeps running in the tray.
    public void Hide() => _appWindow?.Hide();

    public bool IsVisible => _appWindow?.IsVisible == true;

    /// White caption buttons for the dark sign-in and onboarding screens; the theme's own otherwise.
    public void SetCaptionButtonsLight(bool light)
    {
        if (_appWindow == null) return;

        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = light ? Colors.White : null;
        titleBar.ButtonHoverForegroundColor = light ? Colors.White : null;
        titleBar.ButtonHoverBackgroundColor = light ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : null;
        titleBar.ButtonInactiveForegroundColor = light ? Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : null;
    }

    private bool OnGetMinMaxInfo(IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        // Let WinUI fill the maximized size/position first, then raise the minimum track size.
        result = NativeMethods.DefSubclassProc(Hwnd, NativeMethods.WM_GETMINMAXINFO, wParam, lParam);
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        var scale = NativeMethods.GetDpiForWindow(Hwnd) / 96.0;
        info.ptMinTrackSize.X = (int)(MinWidth * scale);
        info.ptMinTrackSize.Y = (int)(MinHeight * scale);
        System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
        return true;
    }
}
