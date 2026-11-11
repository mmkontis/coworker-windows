using Windows.UI;

namespace Coworker.Services.VoiceCommands;

/// The server stores an SF Symbol name and a #RRGGBB color per command and per catalog action. This
/// maps them to a Segoe Fluent Icons glyph and a color; anything unknown falls back to the lightning
/// bolt (the server's default, bolt.fill) and system blue, like the Mac's Color(hex:) fallback.
public static class CommandIcons
{
    private const string DefaultGlyph = "";

    private static readonly Color SystemBlue = Color.FromArgb(0xFF, 0x00, 0x7A, 0xFF);

    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["bolt.fill"] = "",
        ["globe"] = "",
        ["app.badge"] = "",
        ["app.dashed"] = "",
        ["square.grid.2x2"] = "",
        ["phone.fill"] = "",
        ["bubble.left.fill"] = "",
        ["bubble.left.and.bubble.right.fill"] = "",
        ["message.fill"] = "",
        ["envelope"] = "",
        ["envelope.fill"] = "",
        ["envelope.open.fill"] = "",
        ["calendar"] = "",
        ["calendar.badge.plus"] = "",
        ["calendar.badge.clock"] = "",
        ["magnifyingglass"] = "",
        ["cloud.sun.fill"] = "",
        ["clock"] = "",
        ["clock.fill"] = "",
        ["function"] = "",
        ["mic.fill"] = "",
        ["doc.text.fill"] = "",
        ["doc.fill"] = "",
        ["photo.fill"] = "",
        ["sparkles"] = "",
        ["curlybraces"] = "",
        ["network"] = "",
        ["arrow.triangle.branch"] = "",
        ["puzzlepiece.fill"] = "",
        ["xmark.circle.fill"] = "",
        ["checkmark.circle.fill"] = "",
    };

    public static string Glyph(string? symbolName) =>
        symbolName != null && Glyphs.TryGetValue(symbolName, out var glyph) ? glyph : DefaultGlyph;

    /// #RRGGBB (six hex digits, the leading # optional) or null.
    public static Color? TryParseColor(string? hex)
    {
        var text = hex?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text[0] == '#') text = text[1..];
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var rgb))
            return null;

        return Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public static Color ParseColor(string? hex) => TryParseColor(hex) ?? SystemBlue;

    /// The color at a given opacity (the Mac tints tiles with color.opacity(0.12) and so on).
    public static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);
}
