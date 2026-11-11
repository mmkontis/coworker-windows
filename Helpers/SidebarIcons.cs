using System.Globalization;
using Coworker.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Helpers;

/// Icons of the sidebar. Most come from Segoe Fluent Icons (AppTabs.Glyph); the sound wave and the
/// branch, which the icon font has no glyph for, are drawn here (SF Symbols "waveform" and
/// "arrow.triangle.branch" on the Mac).
internal static class SidebarIcons
{
    private const double Size = 16;

    public static IconElement For(AppTab tab) => tab switch
    {
        AppTab.Transcribe => Waveform(),
        AppTab.Workflows => Branch(),
        _ => new FontIcon { Glyph = AppTabs.Get(tab).Glyph, FontSize = Size },
    };

    public static IconElement For(WorkspaceArea area) => area switch
    {
        WorkspaceArea.Transcribe => Waveform(),
        _ => Branch(),
    };

    /// Five rounded bars of different heights.
    public static IconElement Waveform()
    {
        var bars = new (double X, double Height)[] { (2, 5), (5, 10), (8, 15), (11, 9), (14, 4) };
        return Icon(string.Concat(bars.Select(b => Bar(b.X, b.Height))));
    }

    /// Two nodes on a trunk and a third one on the branch that splits off it.
    public static IconElement Branch() => Icon(
        "F0 " + Ring(3.6, 3.2, 1.9, 0.9) + Ring(3.6, 12.8, 1.9, 0.9) + Ring(12.4, 4.4, 1.9, 0.9) +
        "M2.9,5 H4.3 V11 H2.9 Z " +
        "M2.9,10.6 C2.9,7.2 11.7,8.6 11.7,6.2 H13.1 C13.1,10 5.3,8.4 5.3,10.6 Z");

    private static PathIcon Icon(string path) => new()
    {
        Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path),
        Width = Size,
        Height = Size,
    };

    // Fill-only outlines: PathIcon paints its geometry with the foreground brush.
    private static string Bar(double x, double height)
    {
        const double r = 0.8;
        var top = 8 - height / 2;
        var bottom = 8 + height / 2;
        return string.Create(CultureInfo.InvariantCulture,
            $"M{x - r},{top + r} A{r},{r} 0 0 1 {x + r},{top + r} L{x + r},{bottom - r} A{r},{r} 0 0 1 {x - r},{bottom - r} Z ");
    }

    private static string Ring(double cx, double cy, double outer, double inner) => string.Create(CultureInfo.InvariantCulture,
        $"M{cx - outer},{cy} A{outer},{outer} 0 1 1 {cx + outer},{cy} A{outer},{outer} 0 1 1 {cx - outer},{cy} Z " +
        $"M{cx - inner},{cy} A{inner},{inner} 0 1 0 {cx + inner},{cy} A{inner},{inner} 0 1 0 {cx - inner},{cy} Z ");
}
