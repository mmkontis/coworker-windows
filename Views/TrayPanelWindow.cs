using Coworker.Controls;
using Coworker.Helpers;
using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace Coworker.Views;

/// <summary>
/// The panel that opens from the tray icon (Mac: the NSPopover of the status item): a 340-wide acrylic
/// window beside the taskbar, closed when it loses focus or on Escape. While it is open, the card for
/// the app the user came from sits next to it (Mac ActiveAutoEnterPanelController).
/// </summary>
internal sealed class TrayPanelWindow : FloatingWindow
{
    private const double Margin = 8;       // gap to the screen edge
    private const double CompanionGap = 6; // Mac: the card hangs 6 pt from the popover
    private const int SlideMs = 120;

    private readonly TrayPanel _panel = new();
    private AutoEnterWindow? _companion;
    private RectInt32 _bounds;
    private double _scale = 1;
    private bool _anchoredToBottom = true;
    private long _hiddenAt;

    public TrayPanelWindow() : base(new FloatingWindowOptions { NoActivate = false, Acrylic = true })
    {
        Content = _panel;
        _panel.CloseRequested += Dismiss;
        _panel.SizeChanged += OnPanelSizeChanged;

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            Dismiss();
        };
        _panel.KeyboardAccelerators.Add(escape);

        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) return;
            // Opening a flyout inside the panel moves activation to the flyout's own window: that is not leaving.
            UiThread.Queue.TryEnqueue(() =>
            {
                if (IsOpen && VisualTreeHelper.GetOpenPopupsForXamlRoot(_panel.XamlRoot).Count == 0) Dismiss();
            });
        };
    }

    public bool IsOpen => AppWindow.IsVisible;

    /// True when the panel was just closed by clicking away, so the tray click that follows toggles it shut.
    public bool WasJustDismissed => Environment.TickCount64 - _hiddenAt < 300;

    /// Opens the panel next to the tray icon (<paramref name="icon"/> in screen pixels).
    public async void ShowNear(RectInt32 icon)
    {
        _panel.Refresh();
        _panel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _panel.DesiredSize;

        var display = DisplayInfo.FromCursor();
        _scale = display.Scale;
        var width = (int)Math.Round(size.Width * _scale);
        var height = (int)Math.Round(size.Height * _scale);
        var (x, y) = Position(icon, display.WorkArea, width, height);
        _bounds = new RectInt32(x, y, width, height);

        var target = SessionService.Shared.IsAuthenticated ? ForegroundTracker.Last : null;

        Place(_bounds, _scale);
        SetOpacity(0);
        Present();
        var slide = (int)Math.Round(Margin * _scale);
        if (AnimationsEnabled)
            await AnimateAsync(SlideMs, t =>
            {
                SetOpacity(t);
                MoveTo(_bounds.X, _bounds.Y + (int)((1 - t) * slide * (_anchoredToBottom ? 1 : -1)));
            });
        SetOpacity(1);
        MoveTo(_bounds.X, _bounds.Y);

        ShowCompanion(target, display.WorkArea);
    }

    public void Dismiss()
    {
        if (!IsOpen) return;
        _hiddenAt = Environment.TickCount64;
        _companion?.Close();
        _companion = null;
        AppWindow.Hide();
    }

    // The panel hugs the taskbar edge the icon sits on, centred on the icon and kept on screen.
    private (int X, int Y) Position(RectInt32 icon, RectInt32 work, int width, int height)
    {
        var margin = (int)Math.Round(Margin * _scale);
        var centerX = icon.X + icon.Width / 2;
        var centerY = icon.Y + icon.Height / 2;
        var right = work.X + work.Width;
        var bottom = work.Y + work.Height;

        _anchoredToBottom = true;
        if (centerY < work.Y)   // taskbar on top
        {
            _anchoredToBottom = false;
            return (Math.Clamp(centerX - width / 2, work.X + margin, right - width - margin), work.Y + margin);
        }
        if (centerX < work.X)   // taskbar on the left
        {
            _anchoredToBottom = false;
            return (work.X + margin, Math.Clamp(centerY - height / 2, work.Y + margin, bottom - height - margin));
        }
        if (centerX >= right)   // taskbar on the right
            return (right - width - margin, Math.Clamp(centerY - height / 2, work.Y + margin, bottom - height - margin));
        if (centerY >= bottom)  // taskbar at the bottom
            return (Math.Clamp(centerX - width / 2, work.X + margin, right - width - margin), bottom - height - margin);

        // The icon is in the overflow flyout: use the corner.
        return (right - width - margin, bottom - height - margin);
    }

    // The translation row changes the height: keep the edge next to the taskbar where it is.
    // The height comes from what the panel needs, not from its arranged size: that follows the window,
    // and the window's client area can come out a pixel short of what was asked, so sizing the window
    // from it shrank the panel a little on every pass until it collapsed.
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsOpen) return;

        _panel.Measure(new Windows.Foundation.Size(_bounds.Width / _scale, double.PositiveInfinity));
        var height = (int)Math.Round(_panel.DesiredSize.Height * _scale);
        if (Math.Abs(height - _bounds.Height) <= 1) return;
        var y = _anchoredToBottom ? _bounds.Y + _bounds.Height - height : _bounds.Y;
        _bounds = new RectInt32(_bounds.X, y, _bounds.Width, height);
        Place(_bounds, _scale);
    }

    private void ShowCompanion(ForegroundApp? target, RectInt32 work)
    {
        _companion?.Close();
        _companion = null;
        if (target == null) return;

        var companion = new AutoEnterWindow(target);
        var width = (int)Math.Round(AutoEnterCard.CardWidth * _scale);
        var height = (int)Math.Round(AutoEnterCard.CardHeight * _scale);
        var gap = (int)Math.Round(CompanionGap * _scale);

        // Below the panel when there is room (taskbar at the top); above it otherwise.
        var below = _bounds.Y + _bounds.Height + gap + height <= work.Y + work.Height;
        var y = _anchoredToBottom || !below ? _bounds.Y - gap - height : _bounds.Y + _bounds.Height + gap;
        companion.Place(new RectInt32(_bounds.X, y, width, height), _scale);
        companion.Present();
        _companion = companion;
    }

    /// The Auto Enter card: a no-activate window, so using it does not take focus from the panel.
    private sealed class AutoEnterWindow : FloatingWindow
    {
        public AutoEnterWindow(ForegroundApp target) : base(new FloatingWindowOptions { CornerRadius = 14, Acrylic = true })
        {
            var card = new AutoEnterCard();
            card.SetTarget(target);
            Content = card;
        }
    }
}
