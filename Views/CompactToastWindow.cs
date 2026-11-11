using Coworker.Controls;
using Coworker.Helpers;
using Coworker.Services;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Coworker.Views;

/// One toast: a no-activate overlay window hosting a <see cref="CompactToastCard"/>. Stacking is
/// done by <see cref="Services.ToastService"/>. Behaviour mirrors CompactNotificationController.swift:
/// 180 ms fade-in, auto-dismiss, swipe sideways to dismiss (>75 px or a fast flick), spring back otherwise.
internal sealed class CompactToastWindow : FloatingWindow
{
    private const int FadeMs = 180;
    private const double DismissDistance = 75;
    private const double FlickDistance = 20;
    private const double FlickVelocity = 650;
    private const double SlideOutDip = 364;

    private readonly double? _durationSeconds;
    private readonly Action? _action;
    private readonly Action<CompactToastWindow> _closed;
    private DispatcherQueueTimer? _expiry;
    private bool _isClosing;

    /// Window x at the start of a drag, in physical pixels.
    private int _dragOriginX;
    private int _y;

    public Guid Id { get; }
    public bool IsDragging { get; private set; }

    public CompactToastWindow(Guid id, ToastKind kind, string? title, string message, double? durationSeconds,
        string? actionTitle, Action? action, Action<CompactToastWindow> closed)
        : base(new FloatingWindowOptions { CornerRadius = 14, Acrylic = true })
    {
        Id = id;
        _durationSeconds = durationSeconds;
        _action = action;
        _closed = closed;

        var card = new CompactToastCard(kind, title, message, actionTitle);
        card.CloseRequested += () => _ = DismissAsync();
        card.ActionInvoked += () =>
        {
            _ = DismissAsync();
            _action?.Invoke();
        };
        card.Dragged += OnDragged;
        card.DragReleased += OnDragReleased;
        Content = card;
    }

    /// Places the toast at the given physical-pixel bounds, then fades it in on the first call.
    public async void Show(RectInt32 bounds, double scale)
    {
        Place(bounds, scale);
        _y = bounds.Y;
        SetOpacity(0);
        Present();
        await FadeInAsync(FadeMs);
        ScheduleExpiry();
    }

    /// Re-stacks the toast without touching one that is being dragged.
    public void Restack(RectInt32 bounds, double scale)
    {
        if (IsDragging || _isClosing) return;
        Place(bounds, scale);
        _y = bounds.Y;
    }

    public async Task DismissAsync(int direction = 0)
    {
        if (_isClosing) return;
        _isClosing = true;
        _expiry?.Stop();
        _closed(this);

        if (direction != 0 && AnimationsEnabled)
        {
            var scale = DisplayInfo.FromWindow(Hwnd).Scale;
            var current = CurrentX();
            var target = current + direction * (int)(SlideOutDip * scale);
            var fade = FadeOutAsync(FadeMs);
            await AnimateAsync(FadeMs, t => MoveTo(current + (int)((target - current) * t), _y));
            await fade;
        }
        else
        {
            await FadeOutAsync(FadeMs);
        }
        Close();
    }

    private int CurrentX() => AppWindow.Position.X;

    private void OnDragged(double dx)
    {
        if (_isClosing) return;
        if (!IsDragging)
        {
            _expiry?.Stop();
            _dragOriginX = CurrentX();
            IsDragging = true;
        }

        var scale = DisplayInfo.FromWindow(Hwnd).Scale;
        MoveTo(_dragOriginX + (int)dx, _y);
        SetOpacity(Math.Max(0.3, 1 - Math.Abs(dx / scale) / CompactToastCard.CardWidth));
    }

    private void OnDragReleased(double dx, double velocity)
    {
        if (!IsDragging || _isClosing) return;

        var scale = DisplayInfo.FromWindow(Hwnd).Scale;
        var dip = dx / scale;
        var flick = Math.Abs(dip) > FlickDistance && Math.Abs(velocity / scale) > FlickVelocity && dx * velocity > 0;
        if (Math.Abs(dip) > DismissDistance || flick)
        {
            _ = DismissAsync(dx < 0 ? -1 : 1);
            return;
        }

        IsDragging = false;
        var from = CurrentX();
        var fromOpacity = Math.Max(0.3, 1 - Math.Abs(dip) / CompactToastCard.CardWidth);
        if (AnimationsEnabled)
        {
            _ = AnimateAsync(280, t =>
            {
                MoveTo(from + (int)((_dragOriginX - from) * t), _y);
                SetOpacity(fromOpacity + (1 - fromOpacity) * t);
            }, SpringBack);
        }
        else
        {
            MoveTo(_dragOriginX, _y);
            SetOpacity(1);
        }
        ScheduleExpiry();
    }

    private void ScheduleExpiry()
    {
        _expiry?.Stop();
        if (_durationSeconds is not { } seconds || _isClosing) return;

        _expiry = UiThread.Queue.CreateTimer();
        _expiry.Interval = TimeSpan.FromSeconds(seconds);
        _expiry.IsRepeating = false;
        _expiry.Tick += (_, _) => _ = DismissAsync();
        _expiry.Start();
    }

    // cubic-bezier(0.2, 0.9, 0.25, 1.15): a slight overshoot, like the Mac spring-back.
    private static double SpringBack(double t)
    {
        const double x1 = 0.2, y1 = 0.9, x2 = 0.25, y2 = 1.15;
        double lo = 0, hi = 1, u = t;
        for (var i = 0; i < 20; i++)
        {
            u = (lo + hi) / 2;
            var x = 3 * (1 - u) * (1 - u) * u * x1 + 3 * (1 - u) * u * u * x2 + u * u * u;
            if (x < t) lo = u; else hi = u;
        }
        return 3 * (1 - u) * (1 - u) * u * y1 + 3 * (1 - u) * u * u * y2 + u * u * u;
    }
}
