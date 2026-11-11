using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Coworker.Helpers;

/// <summary>
/// A soft drop shadow with a chosen colour, blur and offset (SwiftUI's .shadow(color:radius:x:y:)),
/// which XAML's ThemeShadow cannot express. Attach it to an empty element that sits directly behind
/// the shadowed one, with the same size and corners: the shadow is drawn over the host.
/// </summary>
internal static class ElementShadow
{
    /// <param name="host">Empty element behind the shadowed content, sized like it.</param>
    /// <param name="cornerRadius">DIP radius of the shadowed shape.</param>
    /// <param name="blur">Blur radius in DIP (SwiftUI's radius x 2 renders about the same).</param>
    public static void Attach(FrameworkElement host, double cornerRadius, Color color, double blur, double offsetY)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2((float)cornerRadius);
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = compositor.CreateColorBrush(Colors.White);
        var shapeVisual = compositor.CreateShapeVisual();
        shapeVisual.Shapes.Add(shape);

        var surface = compositor.CreateVisualSurface();
        surface.SourceVisual = shapeVisual;

        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = (float)blur;
        shadow.Offset = new Vector3(0, (float)offsetY, 0);
        shadow.Color = color;
        shadow.Mask = compositor.CreateSurfaceBrush(surface);

        var visual = compositor.CreateSpriteVisual();
        visual.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, visual);

        void Resize()
        {
            var size = new Vector2((float)host.ActualWidth, (float)host.ActualHeight);
            visual.Size = size;
            shapeVisual.Size = size;
            geometry.Size = size;
            surface.SourceSize = size;
        }

        host.SizeChanged += (_, _) => Resize();
        Resize();
    }
}
