using SkiaSharp;

namespace Coworker.Bar;

internal enum BarMode
{
    /// The resting pill.
    Idle,

    /// The shortcut went down and the microphone is being opened: the recording pill with a quiet
    /// shimmer where the meter will be, so nothing pretends to listen before it does.
    Starting,

    /// Listening: a pulsing dot and a live level meter.
    Recording,

    /// Recording done: a spinning dot and a flat meter.
    Transcribing,
}

/// Everything that changes from frame to frame.
/// <param name="Level">Input level 0..1 as it drives the dot (already smoothed).</param>
/// <param name="BarHeights">The 15 meter bars in dip, 2 at rest and up to 34 (already smoothed).</param>
/// <param name="SpinnerDegrees">Rotation of the spinner arc while transcribing.</param>
/// <param name="Chat">A chat-completion take: yellow instead of blue (Mac backgroundColorForMode).</param>
/// <param name="Enter">0..1 through the entrance: the idle pill growing into the recording pill, the
/// meter revealed from the centre out. 1 once it has finished (and for any mode change after it).</param>
/// <param name="Time">Milliseconds on a free-running clock, for the shimmer.</param>
internal readonly record struct BarFrame(BarMode Mode, double Level, IReadOnlyList<double> BarHeights, double SpinnerDegrees,
    bool Chat = false, double Enter = 1, long Time = 0);

/// <summary>
/// Draws the floating bar (Mac: IdleRecordingView, ActiveRecordingView and TranscribingView in
/// RecordingViews.swift) into a transparent canvas the size of the bar's window. Sizes, radii, opacities,
/// spacing and shadows are the Mac's, in device-independent units; the layout is then snapped to whole
/// device pixels so the thin meter bars stay crisp at 125 % and 150 % scaling (a Mac draws at 2x, where
/// half-point sizes land on pixels by themselves). The Mac's blurred ultraThinMaterial has no equivalent
/// behind a per-pixel-alpha window, so the pill is a dark glass fill with a soft top highlight, opaque
/// enough that the unblurred desktop behind it doesn't show through as noise.
/// </summary>
internal static class BarRenderer
{
    /// The window is much larger than the pill so the shadow has room; only the pill area reacts to the mouse.
    public const float WindowWidth = 300;
    public const float WindowHeight = 84;

    private const float HorizontalPadding = 12;
    private const float VerticalPadding = 8;
    private const float DotSize = 10;
    private const float DotToBars = 8;        // HStack spacing 2 + Spacer 4 + spacing 2
    private const float BarWidth = 2.5f;
    private const float BarSpacing = 1.5f;
    private const float RestBarHeight = 2;
    private const int BarCount = 15;

    private static readonly SKColor Blue = new(0x0A, 0x84, 0xFF);                 // systemBlue, dark appearance
    private static readonly SKColor Yellow = new(0xFF, 0xD6, 0x0A);               // systemYellow, dark appearance
    private static readonly SKColor Glass = new(0x1C, 0x1C, 0x1E);               // ultraThinMaterial (dark) stand-in
    private static readonly SKColor White = SKColors.White;

    /// The area that starts a drag, in dip: the idle pill and its shadow ring, or the bar's full width while it is active.
    public static SKRect InteractiveRect(BarMode mode)
    {
        var (width, height) = mode == BarMode.Idle ? (120f, 44f) : (WindowWidth, MathF.Min(WindowHeight, 56));
        return SKRect.Create((WindowWidth - width) / 2, (WindowHeight - height) / 2, width, height);
    }

    public static void Draw(SKCanvas canvas, float scale, BarFrame frame)
    {
        canvas.Clear(SKColors.Transparent);
        var layout = new Layout(canvas.DeviceClipBounds.Width, canvas.DeviceClipBounds.Height, scale);

        // Barely visible, but not transparent: Windows sends mouse input only to pixels that are drawn.
        var hit = InteractiveRect(frame.Mode);
        using (var hitPaint = new SKPaint { Color = new SKColor(0, 0, 0, 1) })
            canvas.DrawRect(new SKRect(hit.Left * scale, hit.Top * scale, hit.Right * scale, hit.Bottom * scale), hitPaint);

        if (frame.Mode == BarMode.Idle) DrawIdle(canvas, layout);
        else DrawActive(canvas, layout, frame);
    }

    /// Device-pixel geometry for one display scale. Widths and x positions are whole pixels; the centre line
    /// sits on a pixel boundary so even heights have whole-pixel edges.
    private readonly struct Layout(int width, int height, float scale)
    {
        public readonly int Width = width;
        public readonly float Scale = scale;
        public readonly float CenterY = height / 2;

        /// A dip length in whole device pixels, never less than <paramref name="minimum"/>.
        public int Px(float dip, int minimum = 1) => Math.Max(minimum, (int)MathF.Round(dip * Scale, MidpointRounding.AwayFromZero));

        /// Rounded to an even pixel count so a length centred on <see cref="CenterY"/> has whole-pixel edges.
        public int EvenPx(float dip) => Math.Max(2, 2 * (int)MathF.Round(dip * Scale / 2, MidpointRounding.AwayFromZero));

        public float Dip(float dip) => dip * Scale;
    }

    private static void DrawIdle(SKCanvas canvas, Layout layout)
    {
        var width = layout.Px(60);
        var height = layout.EvenPx(8);
        var left = (layout.Width - width) / 2;
        var pill = new SKRect(left, layout.CenterY - height / 2f, left + width, layout.CenterY + height / 2f);
        var radius = MathF.Min(layout.Dip(10), pill.Height / 2);

        DrawShadow(canvas, pill, radius, Blue.WithAlpha(Alpha(0.15)), layout.Dip(6), layout.Dip(3));
        using (var glass = Fill(Glass.WithAlpha(Alpha(0.62)))) canvas.DrawRoundRect(pill, radius, radius, glass);
        using (var tint = Fill(White.WithAlpha(Alpha(0.05)))) canvas.DrawRoundRect(pill, radius, radius, tint);
        DrawHighlight(canvas, pill, radius, 0.10);
        using (var stroke = Stroke(White.WithAlpha(Alpha(0.18)), MathF.Max(1, layout.Dip(0.5f))))
            canvas.DrawRoundRect(Inset(pill, stroke.StrokeWidth / 2), radius, radius, stroke);
    }

    private static void DrawActive(SKCanvas canvas, Layout layout, BarFrame frame)
    {
        var recording = frame.Mode == BarMode.Recording;
        var starting = frame.Mode == BarMode.Starting;
        var enter = (float)Ease(Math.Clamp(frame.Enter, 0, 1));
        var accent = frame.Chat ? Yellow : Blue;
        var padding = layout.Px(HorizontalPadding);
        var dot = layout.Px(DotSize);
        var dotToBars = layout.Px(DotToBars);
        var barWidth = layout.Px(BarWidth, 2);
        var barPitch = barWidth + layout.Px(BarSpacing);
        var restHeight = layout.EvenPx(RestBarHeight);

        // Bars in device pixels: at rest they sit on the pixel grid, moving they may be fractional (smooth).
        var heights = new float[BarCount];
        for (var i = 0; i < BarCount; i++)
            heights[i] = MathF.Max(restHeight, (float)frame.BarHeights[i] * layout.Scale);

        // The pill is as tall as its content, the dot or the tallest meter bar, plus the padding (SwiftUI HStack).
        var width = 2 * padding + dot + dotToBars + BarCount * barWidth + (BarCount - 1) * (barPitch - barWidth);
        var height = MathF.Max(dot, heights.Max()) + 2 * layout.Px(VerticalPadding);
        var left = (layout.Width - width) / 2;

        // The entrance grows the idle pill (60 x 8) into this one; content fades in over its second half.
        var drawnWidth = Lerp(layout.Px(60), width, enter);
        var drawnHeight = Lerp(layout.EvenPx(8), height, enter);
        var pill = new SKRect((layout.Width - drawnWidth) / 2, layout.CenterY - drawnHeight / 2,
            (layout.Width + drawnWidth) / 2, layout.CenterY + drawnHeight / 2);
        var radius = MathF.Min(layout.Dip(15), pill.Height / 2);
        var contentAlpha = Math.Clamp((enter - 0.45f) / 0.55f, 0, 1);

        DrawShadow(canvas, pill, radius, accent.WithAlpha(Alpha(0.25)), layout.Dip(8), layout.Dip(3));
        using (var glass = Fill(Glass.WithAlpha(Alpha(0.82)))) canvas.DrawRoundRect(pill, radius, radius, glass);
        using (var tint = Fill(accent.WithAlpha(Alpha(0.15)))) canvas.DrawRoundRect(pill, radius, radius, tint);
        DrawHighlight(canvas, pill, radius, 0.08);
        using (var stroke = Stroke(accent.WithAlpha(Alpha(0.35)), MathF.Max(1, layout.Dip(1))))
            canvas.DrawRoundRect(Inset(pill, stroke.StrokeWidth / 2), radius, radius, stroke);

        if (contentAlpha <= 0) return;
        var dotCenter = new SKPoint(left + padding + dot / 2f, layout.CenterY);
        if (recording) DrawRecordingDot(canvas, dotCenter, dot / 2f, frame.Level, accent, contentAlpha);
        else if (starting) DrawRecordingDot(canvas, dotCenter, dot / 2f, 0.5 + 0.5 * Math.Sin(frame.Time / 180.0), accent, contentAlpha * 0.85f);
        else DrawSpinnerDot(canvas, dotCenter, dot / 2f, layout.Dip(1.5f), frame.SpinnerDegrees, accent);

        var barsLeft = left + padding + dot + dotToBars;
        var barRadius = MathF.Min(layout.Dip(1), barWidth / 2f);
        using var barPaint = Fill(White);
        for (var i = 0; i < BarCount; i++)
        {
            // Revealed from the centre out during the entrance.
            var reveal = Math.Clamp((enter - 0.45f - Math.Abs(i - 7) * 0.035f) / 0.2f, 0, 1);
            var opacity = 0.9 * reveal * contentAlpha;

            // Waiting for the microphone: a highlight sweeping across the resting meter instead of levels.
            if (starting)
            {
                var sweep = (frame.Time % 900) / 900.0 * (BarCount + 6) - 3;
                opacity *= 0.35 + 0.55 * Math.Max(0, 1 - Math.Abs(i - sweep) / 3);
            }
            if (opacity <= 0) continue;

            barPaint.Color = White.WithAlpha(Alpha(opacity));
            float x = barsLeft + i * barPitch;
            var bar = new SKRect(x, layout.CenterY - heights[i] / 2, x + barWidth, layout.CenterY + heights[i] / 2);
            canvas.DrawRoundRect(bar, barRadius, barRadius, barPaint);
        }
    }

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;

    // easeOut (cubic): quick to open, gentle to settle.
    private static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

    // Opacity 0.8 + 0.2 x level, size 1 + 0.3 x level.
    private static void DrawRecordingDot(SKCanvas canvas, SKPoint center, float radius, double level, SKColor accent, float opacity = 1)
    {
        using var paint = Fill(accent.WithAlpha(Alpha((0.8 + level * 0.2) * opacity)));
        canvas.DrawCircle(center, radius * (float)(1 + level * 0.3), paint);
    }

    // Opacity 0.9 with a three-quarter arc turning inside it, clipped to the dot.
    private static void DrawSpinnerDot(SKCanvas canvas, SKPoint center, float radius, float lineWidth, double degrees, SKColor accent)
    {
        using var dot = Fill(accent.WithAlpha(Alpha(0.9)));
        canvas.DrawCircle(center, radius, dot);

        canvas.Save();
        using (var clip = new SKPath())
        {
            clip.AddCircle(center.X, center.Y, radius);
            canvas.ClipPath(clip, antialias: true);
        }
        canvas.Translate(center);
        canvas.RotateDegrees((float)degrees);
        using var arc = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = lineWidth,
            StrokeCap = SKStrokeCap.Round,
            Color = White.WithAlpha(Alpha(0.9)),
        };
        canvas.DrawArc(new SKRect(-radius, -radius, radius, radius), 0, 270, false, arc);
        canvas.Restore();
    }

    // The sheen a material has along its top edge, fading out by the middle.
    private static void DrawHighlight(SKCanvas canvas, SKRect pill, float radius, double opacity)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, pill.Top), new SKPoint(0, pill.MidY),
            [White.WithAlpha(Alpha(opacity)), White.WithAlpha(0)], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, Shader = shader };
        canvas.DrawRoundRect(pill, radius, radius, paint);
    }

    // SwiftUI's shadow radius is twice the Gaussian sigma.
    private static void DrawShadow(SKCanvas canvas, SKRect pill, float radius, SKColor color, float blurRadius, float offsetY)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurRadius / 2),
        };
        var shadow = pill;
        shadow.Offset(0, offsetY);
        canvas.DrawRoundRect(shadow, radius, radius, paint);
    }

    private static SKRect Inset(SKRect rect, float by) => new(rect.Left + by, rect.Top + by, rect.Right - by, rect.Bottom - by);

    private static SKPaint Fill(SKColor color) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

    private static SKPaint Stroke(SKColor color, float width) =>
        new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = width };

    private static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
}
