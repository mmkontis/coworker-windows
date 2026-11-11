using System.Runtime.InteropServices;
using Coworker.Helpers;
using Coworker.Models;
using SkiaSharp;

namespace Coworker.Bar;

/// The bar's window in physical pixels, and the display scale it is laid out for. The pill sits in the
/// middle of it (see <see cref="BarRenderer"/>).
public sealed record BarPlacement(int X, int Y, int Width, int Height, double Scale);

/// <summary>
/// The persistent floating recording bar (Mac: RecordingWindow). A layered window with per-pixel alpha,
/// drawn with Skia: it is transparent around the pill, so its shadow is real and mouse clicks on
/// the empty parts reach the app underneath. It never takes focus or activation, stays on top of
/// full-screen apps and follows the user across virtual desktops. The pill idles as a small capsule,
/// can be dragged (the position is kept), and is placed at the bottom centre of the screen the mouse is
/// on when a take starts. It runs on its own thread so it stays smooth while the app is busy.
/// A transparent oversized window (the Mac's technique) was chosen over a shrink-wrapped window with a
/// clipped region: the region has no soft shadow and aliased edges.
/// </summary>
internal sealed class FloatingBar : IDisposable
{
    private const string ClassName = "CoworkerFloatingBar";
    private const uint CmdMode = InputNative.WM_APP + 1;
    private const uint CmdPlace = InputNative.WM_APP + 2;
    private const uint CmdQuit = InputNative.WM_APP + 3;
    private const int DesktopTimer = 2;
    private const int BarTickMs = 80;   // the meter is recomputed every 80 ms
    private const double BarSmoothingMs = 40;     // bars close ~87 % of the gap in one 80 ms tick (Mac: easeInOut 0.08)
    private const double LevelSmoothingMs = 50;   // the dot (Mac: easeInOut 0.1)
    private const double RestHeight = 2;
    private const int BarCount = 15;
    private const int BottomMarginDip = 40;

    private readonly Func<float> _level;
    private readonly RawWindowNative.WndProc _windowProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    // Bar thread only, from here.
    private readonly double[] _barHeights = Enumerable.Repeat(RestHeight, BarCount).ToArray();   // Mac barHeights: the targets
    private readonly double[] _barsShown = Enumerable.Repeat(RestHeight, BarCount).ToArray();    // what is drawn, gliding to them
    private double _levelTarget, _levelShown;
    private long _lastFrameAt;
    private readonly EasedValue _alpha = new(1, 100);
    private long _enterStartedAt = long.MinValue / 2;        // when the idle pill began growing into the recording pill
    private const double EnterMs = 170;                      // about when the start chime is heard
    private readonly BarNative.DesktopFollower _desktops = new();
    private IntPtr _hwnd;
    private BarMode _mode;
    private bool _chat;
    private long _lastBarTick;
    private double _scale = 1;
    private int _x, _y, _width, _height;
    private bool _hasPosition;
    private bool _dragging;
    private (int X, int Y) _grab;
    private IntPtr _surfaceDc, _surfaceBitmap, _surfaceBits;
    private SKSurface? _surface;
    private IntPtr _openHand, _closedHand;
    private bool _framesRunning;
    private volatile BarPlacement? _placement;

    public FloatingBar(Func<float> level)
    {
        _level = level;
        _windowProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "CoworkerBar" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    /// The user dragged the bar: its new top-left corner in physical pixels (UI thread).
    public event Action<int, int>? Moved;

    /// Where the bar's window is right now; any thread. Feedback that replaces the pill (Mac: the
    /// command badge lives in this same window) is centred on it.
    public BarPlacement? Placement => _placement;

    /// <param name="chat">A chat-completion take: the bar turns yellow.</param>
    public void SetMode(BarMode mode, bool chat = false) => Post(CmdMode, (IntPtr)((int)mode | (chat ? ChatFlag : 0)));

    private const int ChatFlag = 0x100;

    /// Puts the bar at the bottom centre of the screen with the mouse, unless it was dragged there already.
    public void PlaceOnCursorDisplay() => Post(CmdPlace, IntPtr.Zero);

    public void Dispose()
    {
        Post(CmdQuit, IntPtr.Zero);
        _thread.Join(2000);
    }

    private void Post(uint command, IntPtr wParam)
    {
        if (_hwnd != IntPtr.Zero) InputNative.PostMessage(_hwnd, command, wParam, IntPtr.Zero);
    }

    // ===== Bar thread =====

    private void Run()
    {
        var instance = InputNative.GetModuleHandle(null);
        var wndClass = new RawWindowNative.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<RawWindowNative.WNDCLASSEX>(),
            lpfnWndProc = _windowProc,
            hInstance = instance,
            hCursor = RawWindowNative.LoadCursor(IntPtr.Zero, RawWindowNative.IDC_ARROW),
            lpszClassName = ClassName,
        };
        RawWindowNative.RegisterClassEx(ref wndClass);

        // Never activates, never in Alt+Tab, always on top; layered for per-pixel alpha.
        var hwnd = RawWindowNative.CreateWindowEx(
            RawWindowNative.WS_EX_LAYERED | RawWindowNative.WS_EX_TOPMOST | RawWindowNative.WS_EX_TOOLWINDOW | RawWindowNative.WS_EX_NOACTIVATE,
            ClassName, "Coworker dictation", RawWindowNative.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _hwnd = hwnd;

        Place(initial: true);
        RawWindowNative.ShowWindow(hwnd, RawWindowNative.SW_SHOWNOACTIVATE);
        InputNative.SetTimer(hwnd, (UIntPtr)DesktopTimer, 1000, null);
        _ready.Set();

        // Idle, the thread sleeps in GetMessage. Animating, it drains the queue and draws one frame per
        // display refresh: WM_TIMER can't do that (its 15.6 ms granularity turns a 16 ms timer into ~31 ms,
        // which is what made the meter look choppy).
        while (true)
        {
            if (!_framesRunning)
            {
                if (InputNative.GetMessage(out var msg, IntPtr.Zero, 0, 0) <= 0) return;
                InputNative.TranslateMessage(ref msg);
                InputNative.DispatchMessage(ref msg);
                continue;
            }

            while (BarNative.PeekMessage(out var msg, IntPtr.Zero, 0, 0, BarNative.PM_REMOVE))
            {
                if (msg.message == InputNative.WM_QUIT) return;
                InputNative.TranslateMessage(ref msg);
                InputNative.DispatchMessage(ref msg);
            }
            if (!_framesRunning) continue;

            OnFrame();
            BarNative.WaitForNextFrame();
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case CmdMode:
                _chat = ((int)wParam & ChatFlag) != 0;
                SetModeCore((BarMode)((int)wParam & ~ChatFlag));
                Render();
                return IntPtr.Zero;
            case CmdPlace:
                Place(initial: false);
                return IntPtr.Zero;
            case CmdQuit:
                RawWindowNative.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case InputNative.WM_TIMER:
                DesktopCheck();
                return IntPtr.Zero;

            case RawWindowNative.WM_MOUSEACTIVATE:
                return (IntPtr)RawWindowNative.MA_NOACTIVATE;
            case RawWindowNative.WM_SETCURSOR:
                RawWindowNative.SetCursor(_dragging ? _closedHand : _openHand);
                return (IntPtr)1;

            case RawWindowNative.WM_LBUTTONDOWN:
                BeginDrag();
                return IntPtr.Zero;
            case RawWindowNative.WM_MOUSEMOVE:
                if (_dragging) DragTo();
                return IntPtr.Zero;
            case RawWindowNative.WM_LBUTTONUP:
            case RawWindowNative.WM_CAPTURECHANGED:
                EndDrag();
                return IntPtr.Zero;

            case RawWindowNative.WM_DPICHANGED:
            case RawWindowNative.WM_DISPLAYCHANGE:
                Place(initial: false, keepCentre: true);
                return IntPtr.Zero;

            case RawWindowNative.WM_DESTROY:
                Teardown();
                InputNative.PostMessage(IntPtr.Zero, InputNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
        }
        return RawWindowNative.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ===== Mode and animation =====

    private void SetModeCore(BarMode mode)
    {
        if (_mode == mode) return;
        var fromIdle = _mode == BarMode.Idle;
        _mode = mode;

        // A take starting: grow out of the idle pill rather than popping to full size.
        var now0 = Environment.TickCount64;
        if (fromIdle && mode != BarMode.Idle) _enterStartedAt = now0;

        // The meter settles back to flat (transcribing) or starts from rest (a new take, like a freshly
        // appeared SwiftUI view).
        Array.Fill(_barHeights, RestHeight);
        _levelTarget = 0;
        if (mode is BarMode.Starting or BarMode.Recording && fromIdle)
        {
            Array.Fill(_barsShown, RestHeight);
            _levelShown = 0;
        }
        _lastBarTick = 0;
        _lastFrameAt = Environment.TickCount64;
        RawWindowNative.SetWindowText(_hwnd, mode switch
        {
            BarMode.Starting => "Coworker dictation: starting",
            BarMode.Recording => "Coworker dictation: recording",
            BarMode.Transcribing => "Coworker dictation: transcribing",
            _ => "Coworker dictation",
        });
        EnsureFrames();
        Render();
    }

    private void EnsureFrames()
    {
        var now = Environment.TickCount64;
        var animating = _mode != BarMode.Idle || _dragging || _alpha.IsMoving(now) || now - _enterStartedAt < EnterMs;
        if (animating && !_framesRunning) _lastFrameAt = Environment.TickCount64;
        _framesRunning = animating;
    }

    private void OnFrame()
    {
        var now = Environment.TickCount64;
        if (_mode == BarMode.Recording && now - _lastBarTick >= BarTickMs)
        {
            _lastBarTick = now;
            UpdateMeter();
        }

        // Each drawn value closes a fixed share of the gap per millisecond, so a new target mid-glide
        // carries on smoothly instead of restarting from a standstill.
        var elapsed = Math.Clamp(now - _lastFrameAt, 0, 100);
        _lastFrameAt = now;
        var barStep = 1 - Math.Exp(-elapsed / BarSmoothingMs);
        for (var i = 0; i < BarCount; i++) _barsShown[i] += (_barHeights[i] - _barsShown[i]) * barStep;
        _levelShown += (_levelTarget - _levelShown) * (1 - Math.Exp(-elapsed / LevelSmoothingMs));

        Render();
        EnsureFrames();
    }

    // Each bar chases a target from the input level: the centre bars most, each with random variation.
    // Livelier than the Mac's defaults (max 18, volatility 1, variation 0.7-1.3, damping 0.6) by request:
    // taller bars, a wider spread between neighbours, a quicker follow, and a curve that lifts normal
    // speaking volume instead of saving the height for shouting. 34 dip + padding still fits the window.
    private const double MeterMaxHeight = 24, MeterPeakHeight = 34, MeterVolatility = 1.6, MeterDamping = 0.75;

    private void UpdateMeter()
    {
        var level = Math.Clamp(_level(), 0f, 1f);
        _levelTarget = level;
        var lifted = Math.Pow(level, 0.7);

        for (var i = 0; i < BarCount; i++)
        {
            var sensitivity = 1.0 - Math.Abs(i - 7) / 10.0;
            var variation = 0.5 + Random.Shared.NextDouble();
            var target = RestHeight + lifted * sensitivity * variation * (MeterMaxHeight - RestHeight) * MeterVolatility;
            var height = _barHeights[i] + (target - _barHeights[i]) * MeterDamping;
            _barHeights[i] = Math.Clamp(height, RestHeight, MeterPeakHeight);
        }
    }

    private BarFrame BuildFrame(long now) => new(_mode, _levelShown, _barsShown, now % 1000 / 1000.0 * 360, _chat, Math.Clamp((now - _enterStartedAt) / EnterMs, 0, 1), now);

    private void Render()
    {
        if (_surface == null || _hwnd == IntPtr.Zero) return;

        var now = Environment.TickCount64;
        BarRenderer.Draw(_surface.Canvas, (float)_scale, BuildFrame(now));

        var position = new NativeMethods.POINT { X = _x, Y = _y };
        var size = new BarNative.SIZE(_width, _height);
        var source = new NativeMethods.POINT();
        var blend = new BarNative.BLENDFUNCTION
        {
            BlendOp = BarNative.AC_SRC_OVER,
            SourceConstantAlpha = (byte)Math.Round(_alpha.At(now) * 255),
            AlphaFormat = BarNative.AC_SRC_ALPHA,
        };
        BarNative.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref position, ref size, _surfaceDc, ref source, 0, ref blend, BarNative.ULW_ALPHA);
    }

    // ===== Placement =====

    private readonly record struct Screen(NativeMethods.RECT Monitor, NativeMethods.RECT Work, double Scale);

    private static Screen ScreenAt(NativeMethods.POINT point)
    {
        var monitor = NativeMethods.MonitorFromPoint(point, 2);   // nearest
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        var scale = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        return new Screen(info.rcMonitor, info.rcWork, scale);
    }

    private static bool Contains(NativeMethods.RECT rect, int x, int y) => x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;

    /// Bottom centre of the work area, 40 dip above its bottom edge (Mac: defaultFrame).
    private (int X, int Y) DefaultPosition(Screen screen, int width, int height) => (
        screen.Work.Left + (screen.Work.Right - screen.Work.Left - width) / 2,
        screen.Work.Bottom - (int)Math.Round(BottomMarginDip * screen.Scale) - height);

    private void Place(bool initial, bool keepCentre = false)
    {
        NativeMethods.GetCursorPos(out var cursor);
        var screen = ScreenAt(cursor);
        var settings = AppSettings.Instance;

        var scale = screen.Scale;
        var width = (int)Math.Round(BarRenderer.WindowWidth * scale);
        var height = (int)Math.Round(BarRenderer.WindowHeight * scale);
        int x, y;

        if (initial && settings.FloatingBarX is { } savedX && settings.FloatingBarY is { } savedY)
        {
            // Where the user left it, if that spot still exists (a monitor may have been unplugged).
            var saved = ScreenAt(new NativeMethods.POINT { X = savedX + width / 2, Y = savedY + height / 2 });
            scale = saved.Scale;
            width = (int)Math.Round(BarRenderer.WindowWidth * scale);
            height = (int)Math.Round(BarRenderer.WindowHeight * scale);
            (x, y) = Contains(saved.Monitor, savedX + width / 2, savedY + height / 2) ? (savedX, savedY) : DefaultPosition(screen, width, height);
        }
        else if (initial || !_hasPosition)
        {
            (x, y) = DefaultPosition(screen, width, height);
        }
        else if (keepCentre)
        {
            // The display's scale changed: keep the pill where it is.
            var here = ScreenAt(new NativeMethods.POINT { X = _x + _width / 2, Y = _y + _height / 2 });
            scale = here.Scale;
            width = (int)Math.Round(BarRenderer.WindowWidth * scale);
            height = (int)Math.Round(BarRenderer.WindowHeight * scale);
            (x, y) = (_x + _width / 2 - width / 2, _y + _height / 2 - height / 2);
        }
        else if (Contains(screen.Monitor, _x + _width / 2, _y + _height / 2))
        {
            (x, y) = (_x, _y);   // still on the screen with the mouse: leave it where it is
        }
        else
        {
            (x, y) = DefaultPosition(screen, width, height);
        }

        (_x, _y) = (x, y);
        _hasPosition = true;
        if (width != _width || height != _height || Math.Abs(scale - _scale) > 0.001) ResizeSurface(width, height, scale);
        _placement = new BarPlacement(_x, _y, _width, _height, _scale);

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        Render();
    }

    private void ResizeSurface(int width, int height, double scale)
    {
        DisposeSurface();
        _width = width;
        _height = height;
        _scale = scale;
        (_surfaceDc, _surfaceBitmap, _surfaceBits) = BarNative.CreateSurface(width, height);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _surfaceBits, info.RowBytes);

        DestroyCursors();
        var size = (int)Math.Round(32 * scale);
        _openHand = HandCursors.Create(closed: false, size);
        _closedHand = HandCursors.Create(closed: true, size);
    }

    private void DesktopCheck()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        _desktops.Follow(_hwnd, foreground);
    }

    // ===== Mouse =====

    // Only the drawn pixels take the mouse (the renderer paints the interactive area, nearly invisibly);
    // everywhere else Windows hands clicks to the app underneath.
    private void BeginDrag()
    {
        NativeMethods.GetCursorPos(out var cursor);
        _grab = (cursor.X - _x, cursor.Y - _y);
        _dragging = true;
        RawWindowNative.SetCapture(_hwnd);
        _alpha.MoveTo(0.9, Environment.TickCount64, 100);
        RawWindowNative.SetCursor(_closedHand);
        EnsureFrames();
    }

    private void DragTo()
    {
        NativeMethods.GetCursorPos(out var cursor);
        _x = cursor.X - _grab.X;
        _y = cursor.Y - _grab.Y;
        _placement = new BarPlacement(_x, _y, _width, _height, _scale);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _x, _y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        Render();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        RawWindowNative.ReleaseCapture();
        _alpha.MoveTo(1.0, Environment.TickCount64, 200);
        EnsureFrames();

        var (x, y) = (_x, _y);
        UiThread.Post(() => Moved?.Invoke(x, y));
    }

    // ===== Cleanup =====

    private void DisposeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_surfaceBitmap != IntPtr.Zero) BarNative.DeleteObject(_surfaceBitmap);
        if (_surfaceDc != IntPtr.Zero) BarNative.DeleteDC(_surfaceDc);
        _surfaceBitmap = _surfaceDc = _surfaceBits = IntPtr.Zero;
    }

    private void DestroyCursors()
    {
        if (_openHand != IntPtr.Zero) RawWindowNative.DestroyCursor(_openHand);
        if (_closedHand != IntPtr.Zero) RawWindowNative.DestroyCursor(_closedHand);
        _openHand = _closedHand = IntPtr.Zero;
    }

    private void Teardown()
    {
        _framesRunning = false;
        InputNative.KillTimer(_hwnd, (UIntPtr)DesktopTimer);
        DisposeSurface();
        DestroyCursors();
    }
}
