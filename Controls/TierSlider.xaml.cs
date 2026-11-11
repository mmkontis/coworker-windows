using System.Numerics;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Coworker.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace Coworker.Controls;

/// The transcription model slider of the tray panel (see TierSlider.xaml). Dragging snaps towards the
/// nearest of the three stops, the knob springs to the chosen one, arrow keys step it; picking a
/// model stores it as the user's own choice.
public sealed partial class TierSlider : UserControl
{
    private const double KnobSize = 34;
    private const int StopCount = 3;

    private readonly Ellipse[] _dots = new Ellipse[StopCount];
    private double _position;          // 0..1, animated
    private double? _dragPosition;

    public TierSlider()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "Transcription model");

        for (var stop = 0; stop < StopCount; stop++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, CenterPoint = new Vector3(3, 3, 0) };
            Canvas.SetTop(dot, 14);
            _dots[stop] = dot;
            Dots.Children.Add(dot);
        }

        ElementShadow.Attach(KnobShadow, 17, Color.FromArgb(0x29, 0, 0, 0), 8, 2);
        ToolTipService.SetToolTip(FasterButton, TrayOptions.ModelTiers[0].Name);
        ToolTipService.SetToolTip(SmarterButton, TrayOptions.ModelTiers[^1].Name);

        _position = Index / 2.0;
        AppSettings.Changed += () => UiThread.Post(() =>
        {
            if (_dragPosition == null) _position = Index / 2.0;
            Update();
        });
        KeyDown += OnKeyDown;
        LostFocus += (_, _) => SetKeyboardAdjusting(false);
    }

    /// The stop of the stored model; a model that is not one of the stops shows the middle one (Mac).
    private static int Index
    {
        get
        {
            var index = Array.FindIndex(TrayOptions.ModelTiers, t => t.Code == AppSettings.Instance.SelectedModel);
            return index < 0 ? 1 : index;
        }
    }

    private static string ModelName(int stop) => TrayOptions.ModelTiers[stop].Name;

    internal int CurrentStop => Index;

    /// Sets the model to a stop (0-2) and springs the knob there.
    internal async void Choose(int stop)
    {
        stop = Math.Clamp(stop, 0, StopCount - 1);
        var from = _dragPosition ?? _position;
        var to = stop / 2.0;
        _dragPosition = null;

        TrayOptions.SelectModel(TrayOptions.ModelTiers[stop].Code);
        (FrameworkElementAutomationPeer.FromElement(this) as TierSliderPeer)?.ValueChanged();

        if (!FloatingWindow.AnimationsEnabled)
        {
            _position = to;
            Update();
            return;
        }

        await FloatingWindow.AnimateAsync(600, t =>
        {
            _position = from + (to - from) * t;
            Update();
        }, Spring);
        _position = to;
        Update();
    }

    // Damped spring (Mac: response 0.32, damping 0.72) as a progress curve that overshoots slightly.
    private static double Spring(double t)
    {
        const double zeta = 0.72;
        var omega = 2 * Math.PI / 0.32;
        var damped = omega * Math.Sqrt(1 - zeta * zeta);
        var seconds = t * 0.6;
        return 1 - Math.Exp(-zeta * omega * seconds) *
            (Math.Cos(damped * seconds) + zeta * omega / damped * Math.Sin(damped * seconds));
    }

    // MARK: - Drawing

    private void Update()
    {
        var travel = Math.Max(1, TrackArea.ActualWidth - KnobSize);
        var dragging = _dragPosition != null && FloatingWindow.AnimationsEnabled;
        var position = _dragPosition ?? _position;
        var center = KnobSize / 2 + position * travel;

        Fill.Width = Math.Max(0, center);
        KnobHost.Margin = new Thickness(center - KnobSize / 2, 0, 0, 0);

        for (var stop = 0; stop < StopCount; stop++)
        {
            var distance = (position - stop / 2.0) * travel;
            var near = Math.Max(0, 1 - Math.Abs(distance) / 28);
            var attraction = near * near * (3 - 2 * near);
            var shift = dragging ? Math.Clamp(distance * 0.2, -3, 3) * attraction : 0;
            var scaleNear = Math.Max(0, 1 - Math.Abs(position * 2 - stop) / 0.42);
            var scale = dragging ? 1 + 2.4 * scaleNear * scaleNear : 1;

            var dot = _dots[stop];
            Canvas.SetLeft(dot, 14 + stop / 2.0 * travel + shift);
            dot.Scale = new Vector3((float)scale, (float)scale, 1);
            var passed = stop / 2.0 < position;
            dot.Fill = passed
                ? new SolidColorBrush(Colors.White)
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            dot.Opacity = passed ? 0.55 : 0.6;
        }

        var index = Index;
        StyleEnd(FasterText, FasterIcon, index == 0);
        StyleEnd(SmarterText, SmarterIcon, index == StopCount - 1);
        AutomationProperties.SetItemStatus(FasterButton, index == 0 ? "Selected" : "");
        AutomationProperties.SetItemStatus(SmarterButton, index == StopCount - 1 ? "Selected" : "");
    }

    private static void StyleEnd(TextBlock text, FontIcon icon, bool active)
    {
        var brush = (Brush)Application.Current.Resources[active ? "CwAccentBrush" : "TextFillColorSecondaryBrush"];
        text.Foreground = brush;
        icon.Foreground = brush;
        text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update();

    // MARK: - Input

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SetKeyboardAdjusting(false);
        TrackArea.CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        Drag(e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPosition != null) Drag(e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPosition == null) return;
        TrackArea.ReleasePointerCapture(e.Pointer);
        Choose((int)Math.Round(_dragPosition.Value * 2));
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _dragPosition = null;
        Update();
    }

    // Mac drag: the knob follows the pointer with a 22% pull towards the nearest stop within 20 px.
    private void Drag(PointerRoutedEventArgs e)
    {
        var travel = Math.Max(1, TrackArea.ActualWidth - KnobSize);
        var x = e.GetCurrentPoint(TrackArea).Position.X;
        var raw = Math.Clamp((x - KnobSize / 2) / travel, 0, 1);
        var nearest = Math.Round(raw * 2) / 2;
        var proximity = Math.Max(0, 1 - Math.Abs(raw - nearest) * travel / 20);
        _dragPosition = raw + (nearest - raw) * proximity * 0.22;
        Update();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Down => -1,
            VirtualKey.Right or VirtualKey.Up => 1,
            _ => 0,
        };
        if (step == 0) return;

        e.Handled = true;
        SetKeyboardAdjusting(true);
        Choose(Index + step);
    }

    private void SetKeyboardAdjusting(bool on) =>
        FocusRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private void OnFasterClick(object sender, RoutedEventArgs e) => Choose(0);

    private void OnSmarterClick(object sender, RoutedEventArgs e) => Choose(StopCount - 1);

    protected override AutomationPeer OnCreateAutomationPeer() => new TierSliderPeer(this);

    /// Screen readers see a slider with three values.
    private sealed class TierSliderPeer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        private readonly TierSlider _owner;
        private int _reported;

        public TierSliderPeer(TierSlider owner) : base(owner)
        {
            _owner = owner;
            _reported = owner.CurrentStop;
        }

        protected override string GetClassNameCore() => nameof(TierSlider);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string GetNameCore() => "Transcription model";

        protected override string GetItemStatusCore() => ModelName(_owner.CurrentStop);

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

        public bool IsReadOnly => false;
        public double LargeChange => 1;
        public double Maximum => StopCount - 1;
        public double Minimum => 0;
        public double SmallChange => 1;
        public double Value => _owner.CurrentStop;

        public void SetValue(double value) => _owner.Choose((int)Math.Round(value));

        public void ValueChanged()
        {
            var now = _owner.CurrentStop;
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)_reported, (double)now);
            _reported = now;
        }
    }
}
