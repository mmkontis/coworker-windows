using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Models;
using Windows.Graphics;

namespace Coworker.Views;

/// Where the voice-command feedback windows sit: centred on the floating recording bar, which they replace.
internal static class CommandBarPlacement
{
    private const int MarginDip = 12;

    public static (RectInt32 Bounds, double Scale) Compute(IntPtr focusedWindow, int widthDip, int heightDip)
    {
        var bar = DictationService.Shared.BarLocation;
        var display = focusedWindow != IntPtr.Zero ? DisplayInfo.FromWindow(focusedWindow) : DisplayInfo.Primary;
        var scale = bar?.Scale ?? display.Scale;
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);
        var work = display.WorkArea;
        var (x, y) = bar != null
            ? (bar.X + bar.Width / 2 - width / 2, bar.Y + bar.Height / 2 - height / 2)
            : (work.X + (work.Width - width) / 2, work.Y + work.Height - height - (int)Math.Round(MarginDip * scale));
        return (new RectInt32(x, y, width, height), scale);
    }
}
