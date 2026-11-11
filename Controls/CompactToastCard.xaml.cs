using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Coworker.Controls;

/// The toast body (Mac CompactNotificationCard): app icon, one-line message with middle truncation,
/// optional accent action button, close button. Horizontal drags on the body are reported for
/// swipe-to-dismiss; the window layer moves the window.
public sealed partial class CompactToastCard : UserControl
{
    public const double CardWidth = 340;
    public const double CardHeight = 52;

    // Card padding 24 + border 2 + icon 23 + gaps 9 + 9 + close 18, plus a little slack for font differences.
    private const double FixedWidth = 24 + 2 + 23 + 9 + 9 + 18 + 8;

    public event Action? CloseRequested;
    public event Action? ActionInvoked;

    /// Screen-space horizontal distance moved since the press.
    public event Action<double>? Dragged;

    /// Total distance and release velocity (px/s), 0 when the pointer rested before letting go.
    public event Action<double, double>? DragReleased;

    private double _startX;
    private double _lastX;
    private long _lastTick;
    private double _velocity;
    private bool _dragging;

    public CompactToastCard(ToastKind kind, string? title, string message, string? actionTitle)
    {
        InitializeComponent();

        var messageFont = string.IsNullOrEmpty(title) ? 12.0 : 11.0;
        var available = CardWidth - FixedWidth;
        if (!string.IsNullOrEmpty(actionTitle))
        {
            ActionText.Text = actionTitle;
            ActionButton.Visibility = Visibility.Visible;
            available -= Measure(actionTitle, 11, Microsoft.UI.Text.FontWeights.Medium) + 20 + 9;
        }

        if (!string.IsNullOrEmpty(title))
        {
            TitleText.Text = title;
            TitleText.Visibility = Visibility.Visible;
            MessageText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        MessageText.FontSize = messageFont;
        MessageText.Text = MiddleTruncate(message, available, messageFont);

        ToolTipService.SetToolTip(DragSurface, message);
        AutomationProperties.SetName(DragSurface, string.IsNullOrEmpty(title) ? message : $"{title}. {message}");
        AutomationProperties.SetLiveSetting(DragSurface,
            kind is ToastKind.Error or ToastKind.Warning ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }

    /// Mac truncationMode(.middle): XAML only trims the end, so cut the middle by measuring.
    private static string MiddleTruncate(string text, double maxWidth, double fontSize)
    {
        if (Measure(text, fontSize, Microsoft.UI.Text.FontWeights.Normal) <= maxWidth) return text;

        int lo = 0, hi = text.Length;   // characters kept in total
        while (lo < hi)
        {
            var keep = (lo + hi + 1) / 2;
            if (Measure(Middle(text, keep), fontSize, Microsoft.UI.Text.FontWeights.Normal) <= maxWidth) lo = keep;
            else hi = keep - 1;
        }
        return Middle(text, lo);
    }

    private static string Middle(string text, int keep)
    {
        if (keep >= text.Length) return text;
        var head = (keep + 1) / 2;
        var tail = keep / 2;
        return text[..head] + "…" + text[^tail..];
    }

    private static double Measure(string text, double fontSize, Windows.UI.Text.FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = fontSize, FontWeight = weight };
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionInvoked?.Invoke();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Helpers.NativeMethods.GetCursorPos(out var pt);
        _startX = _lastX = pt.X;
        _lastTick = Environment.TickCount64;
        _velocity = 0;
        _dragging = true;
        DragSurface.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        Helpers.NativeMethods.GetCursorPos(out var pt);
        var now = Environment.TickCount64;
        _velocity = (pt.X - _lastX) / Math.Max(0.001, (now - _lastTick) / 1000.0);
        _lastX = pt.X;
        _lastTick = now;
        Dragged?.Invoke(pt.X - _startX);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DragSurface.ReleasePointerCapture(e.Pointer);

        Helpers.NativeMethods.GetCursorPos(out var pt);
        var restedMs = Environment.TickCount64 - _lastTick;
        DragReleased?.Invoke(pt.X - _startX, restedMs < 80 ? _velocity : 0);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DragReleased?.Invoke(0, 0);
    }
}
