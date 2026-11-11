using System.Numerics;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services.VoiceCommands;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Coworker.Views;

/// The "command executed" badge (Mac: CommandExecutedView in RecordingViews.swift): the command's icon
/// in a glowing circle, shown where the recording bar was as soon as a command matches, whatever its
/// outcome. Never takes focus; a new badge replaces the current one.
public sealed class CommandExecutedWindow : FloatingWindow
{
    private const int WidthDip = 60;
    private const int HeightDip = 52;
    private const double ShowSeconds = 2.5;

    private static CommandExecutedWindow? _current;

    private readonly Grid _root;
    private readonly ScaleTransform _scale = new() { ScaleX = 0.3, ScaleY = 0.3 };
    private bool _isClosing;

    private CommandExecutedWindow(VoiceCommand command, IntPtr focusedWindow)
        : base(new FloatingWindowOptions { CornerRadius = 15, Acrylic = true })
    {
        var color = CommandIcons.ParseColor(command.IconColor);

        var circle = new Ellipse { Width = 36, Height = 36, Fill = new SolidColorBrush(CommandIcons.WithOpacity(color, 0.15)) };
        var glow = new Grid { Width = 36, Height = 36 };
        glow.Children.Add(circle);
        glow.Children.Add(new FontIcon
        {
            Glyph = CommandIcons.Glyph(command.IconName),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        glow.Loaded += (_, _) => StartGlow(glow, circle, color);

        _root = new Grid
        {
            Background = new SolidColorBrush(CommandIcons.WithOpacity(color, 0.10)),
            BorderBrush = new SolidColorBrush(CommandIcons.WithOpacity(color, 0.30)),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            RenderTransform = _scale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
        _root.Children.Add(glow);
        Content = _root;

        var (bounds, scale) = CommandBarPlacement.Compute(focusedWindow, WidthDip, HeightDip);
        Place(bounds, scale);
    }

    /// Shows the badge for a command for 2.5 seconds. Call on the UI thread.
    public static void ShowFor(VoiceCommand command, IntPtr focusedWindow)
    {
        _current?.Dismiss();
        var window = _current = new CommandExecutedWindow(command, focusedWindow);
        window.Present();
        window.AnimateIn();

        var timer = UiThread.Queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(ShowSeconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => window.Dismiss();
        timer.Start();
    }

    private void Dismiss()
    {
        if (_isClosing) return;
        _isClosing = true;
        if (_current == this) _current = null;
        Close();
    }

    // Spring: response 0.4 s, damping 0.6 (about a 10% overshoot).
    private void AnimateIn()
    {
        var scaleX = new DoubleAnimation { From = 0.3, To = 1, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = new BackEase { Amplitude = 1, EasingMode = EasingMode.EaseOut } };
        var scaleY = new DoubleAnimation { From = 0.3, To = 1, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = new BackEase { Amplitude = 1, EasingMode = EasingMode.EaseOut } };
        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(400) };
        Storyboard.SetTarget(scaleX, _scale);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        Storyboard.SetTarget(scaleY, _scale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        Storyboard.SetTarget(opacity, _root);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        if (!AnimationsEnabled)
        {
            _scale.ScaleX = _scale.ScaleY = 1;
            _root.Opacity = 1;
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Children.Add(opacity);
        storyboard.Begin();
    }

    // A soft halo behind the circle whose blur breathes between 0 and 12 over 1.2 s (ease in/out, reversing).
    private static void StartGlow(FrameworkElement host, Ellipse mask, Color color)
    {
        if (!AnimationsEnabled) return;

        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Color = CommandIcons.WithOpacity(color, 0.15);
        shadow.Offset = Vector3.Zero;
        shadow.Mask = mask.GetAlphaMask();

        var halo = compositor.CreateSpriteVisual();
        halo.Size = new Vector2((float)host.Width, (float)host.Height);
        halo.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, halo);

        var blur = compositor.CreateScalarKeyFrameAnimation();
        blur.InsertKeyFrame(0, 0);
        blur.InsertKeyFrame(1, 12, compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1)));
        blur.Duration = TimeSpan.FromSeconds(1.2);
        blur.Direction = AnimationDirection.Alternate;
        blur.IterationBehavior = AnimationIterationBehavior.Forever;
        shadow.StartAnimation("BlurRadius", blur);
    }
}
