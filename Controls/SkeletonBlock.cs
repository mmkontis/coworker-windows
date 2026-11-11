using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Controls;

/// <summary>
/// A loading placeholder the size of the text it stands in for: a soft rounded bar that slowly pulses,
/// so a number still on its way never shows as 0. Give it the Width and Height of that text.
/// </summary>
public sealed class SkeletonBlock : UserControl
{
    private readonly Border _bar = new();

    public SkeletonBlock()
    {
        CornerRadius = new CornerRadius(6);
        HorizontalAlignment = HorizontalAlignment.Left;
        IsTabStop = false;
        _bar.Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        Content = _bar;
        Loaded += (_, _) =>
        {
            _bar.CornerRadius = CornerRadius;   // set in XAML on the control
            Pulse();
        };
    }

    // On the composition thread: smooth however busy the UI thread is while the data loads.
    private void Pulse()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_bar);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0.45f);
        animation.InsertKeyFrame(0.5f, 1f);
        animation.InsertKeyFrame(1f, 0.45f);
        animation.Duration = TimeSpan.FromMilliseconds(1400);
        animation.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
    }
}
