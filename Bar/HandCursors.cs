using SkiaSharp;

namespace Coworker.Bar;

/// The open and closed hand shown while the pointer is over the bar and while it is dragged (Mac:
/// NSCursor.openHand and .closedHand). Windows only ships a pointing hand, so both are drawn.
internal static class HandCursors
{
    private const float Design = 32;

    /// A cursor handle for the open (<paramref name="closed"/> false) or closed hand, <paramref name="size"/> pixels square.
    public static IntPtr Create(bool closed, int size) =>
        BarNative.CreateCursor(size, bits => Paint(bits, closed, size), size / 2, size / 2);

    private static void Paint(IntPtr bits, bool closed, int size)
    {
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, bits, info.RowBytes);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / Design);

        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 2.6f, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.Black };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };

        // The black outline is every shape grown a little; the white fill on top leaves one outline around the hand.
        foreach (var paint in new[] { outline, fill })
        {
            if (closed) DrawClosed(canvas, paint);
            else DrawOpen(canvas, paint);
        }
    }

    private static void DrawOpen(SKCanvas canvas, SKPaint paint)
    {
        canvas.DrawRoundRect(new SKRect(9, 15, 23, 27), 4, 4, paint);
        var tops = new[] { 8f, 5.5f, 5f, 7.5f };
        for (var i = 0; i < tops.Length; i++)
        {
            var left = 9.2f + i * 3.6f;
            canvas.DrawRoundRect(new SKRect(left, tops[i], left + 3.2f, 19), 1.6f, 1.6f, paint);
        }

        canvas.Save();
        canvas.RotateDegrees(-38, 8.5f, 19);
        canvas.DrawRoundRect(new SKRect(6.9f, 15, 10.1f, 23), 1.6f, 1.6f, paint);
        canvas.Restore();
    }

    private static void DrawClosed(SKCanvas canvas, SKPaint paint)
    {
        canvas.DrawRoundRect(new SKRect(8, 14.5f, 24, 26), 5, 5, paint);
        canvas.DrawCircle(10.8f, 14.5f, 2.6f, paint);
        canvas.DrawCircle(14.6f, 13.6f, 2.6f, paint);
        canvas.DrawCircle(18.4f, 13.6f, 2.6f, paint);
        canvas.DrawCircle(21.6f, 14.5f, 2.6f, paint);
    }
}
