using Coworker.Helpers;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Coworker.Views;

public sealed record FloatingWindowOptions
{
    /// Never take focus or activation (toasts, recording bar). Text-entry panels set this to false.
    public bool NoActivate { get; init; } = true;

    /// Corner radius in DIP. 0 = the native Windows 11 rounding (anti-aliased, ~8 px);
    /// anything else clips the window to a round-rect region (works on Windows 10 too, but aliased).
    public int CornerRadius { get; init; }

    /// Desktop acrylic material (falls back to no backdrop where unsupported).
    public bool Acrylic { get; init; }
}

/// <summary>
/// Base for borderless, always-on-top, taskbar-less overlay windows (toasts, recording bar, tray
/// popup, upgrade window). Styles are set on the raw HWND (WS_POPUP + WS_EX_TOOLWINDOW + TOPMOST
/// [+ NOACTIVATE]) because WinUI's presenter API cannot express a no-activate tool window.
/// Position and size are in physical pixels; convert with <see cref="DisplayInfo"/>.
/// </summary>
public class FloatingWindow : Window
{
    private readonly FloatingWindowOptions _options;
    private SizeInt32 _regionSize;
    private byte _alpha = 255;

    public IntPtr Hwnd { get; }

    public FloatingWindow(FloatingWindowOptions options)
    {
        _options = options;
        Hwnd = WindowNative.GetWindowHandle(this);
        Title = "";
        ExtendsContentIntoTitleBar = true;

        if (options.Acrylic && DesktopAcrylicController.IsSupported())
            SystemBackdrop = new AlwaysActiveAcrylicBackdrop();

        var style = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU |
                   NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
        NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_STYLE, style | NativeMethods.WS_POPUP);

        var exStyle = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE) |
                      NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        if (options.NoActivate) exStyle |= NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        if (options.CornerRadius == 0)
        {
            var preference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(Hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
    }

    /// False when the user turned animations off in Windows settings.
    public static bool AnimationsEnabled => new UISettings().AnimationsEnabled;

    /// Moves and sizes the window (physical pixels) without activating it; <paramref name="scale"/> is
    /// the monitor's DPI scale, used to size the rounded-corner region.
    public void Place(RectInt32 bounds, double scale)
    {
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOACTIVATE);

        var size = new SizeInt32(bounds.Width, bounds.Height);
        if (_options.CornerRadius > 0 && !size.Equals(_regionSize))
        {
            _regionSize = size;
            var diameter = (int)Math.Round(_options.CornerRadius * 2 * scale);
            // The region is owned by the window from here on.
            NativeMethods.SetWindowRgn(Hwnd,
                NativeMethods.CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, diameter, diameter), true);
        }
    }

    public void MoveTo(int x, int y) =>
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// Shows the window; activates it only when the options allow.
    public void Present()
    {
        if (_options.NoActivate) AppWindow.Show(false);
        else Activate();
    }

    /// Whole-window opacity, 0 to 1. The window is only layered (WS_EX_LAYERED) while it is
    /// translucent, so the steady state renders like any other window.
    public void SetOpacity(double opacity)
    {
        _alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

        var exStyle = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE);
        var layered = (exStyle & NativeMethods.WS_EX_LAYERED) != 0;
        if (_alpha == 255)
        {
            if (layered) NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE, exStyle & ~NativeMethods.WS_EX_LAYERED);
            return;
        }

        if (!layered) NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED);
        NativeMethods.SetLayeredWindowAttributes(Hwnd, 0, _alpha, NativeMethods.LWA_ALPHA);
    }

    public Task FadeInAsync(int milliseconds = 180) => FadeAsync(1, milliseconds);

    public Task FadeOutAsync(int milliseconds = 180) => FadeAsync(0, milliseconds);

    private async Task FadeAsync(double target, int milliseconds)
    {
        var from = _alpha / 255.0;
        if (!AnimationsEnabled || milliseconds <= 0)
        {
            SetOpacity(target);
            return;
        }
        await AnimateAsync(milliseconds, t => SetOpacity(from + (target - from) * t));
    }

    /// Runs <paramref name="step"/> with an eased 0-to-1 progress on the UI thread for the given
    /// duration (~60 fps). <paramref name="ease"/> defaults to ease-out cubic.
    public static Task AnimateAsync(int milliseconds, Action<double> step, Func<double, double>? ease = null)
    {
        var completion = new TaskCompletionSource();
        ease ??= t => 1 - Math.Pow(1 - t, 3);
        var timer = UiThread.Queue.CreateTimer();
        var started = Environment.TickCount64;
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (s, _) =>
        {
            var t = Math.Min(1.0, (Environment.TickCount64 - started) / (double)milliseconds);
            step(ease(t));
            if (t >= 1.0)
            {
                s.Stop();
                completion.TrySetResult();
            }
        };
        timer.Start();
        return completion.Task;
    }
}
