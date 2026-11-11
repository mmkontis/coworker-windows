using Coworker.Controls;
using Coworker.Helpers;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Coworker.Views;

/// The "Sign In Required" / "Upgrade Your Plan" panel (Mac UpgradeWindow): a borderless, always-on-top
/// card in the middle of the screen, a little above centre, that fades in and out. It takes clicks but
/// never steals focus from the app the user was dictating into.
internal sealed class UpgradeWindow : FloatingWindow
{
    private const int FadeInMs = 250;
    private const int FadeOutMs = 200;
    private const double RaiseDip = 40;   // Mac: y = midY - h/2 + 40 in bottom-up coordinates

    private bool _isClosing;

    public UpgradeWindow(string message, string upgradeUrl, int? currentUsage, int? limit, Action<string> open)
        : base(new FloatingWindowOptions { CornerRadius = 16, Acrylic = true })
    {
        var card = new UpgradeCard(message, upgradeUrl, currentUsage, limit);
        card.NotNow += () => _ = DismissAsync();
        card.ActionInvoked += () =>
        {
            open(upgradeUrl);
            _ = DismissAsync();
        };
        Content = card;

        card.Measure(new Windows.Foundation.Size(UpgradeCard.CardWidth, double.PositiveInfinity));
        var display = DisplayInfo.FromCursor();
        var width = (int)Math.Round(card.DesiredSize.Width * display.Scale);
        var height = (int)Math.Round(card.DesiredSize.Height * display.Scale);
        var work = display.WorkArea;
        Place(new RectInt32(work.X + (work.Width - width) / 2,
                            work.Y + (work.Height - height) / 2 - (int)Math.Round(RaiseDip * display.Scale), width, height),
              display.Scale);
    }

    /// Fades the window in.
    public async void Show()
    {
        SetOpacity(0);
        Present();
        await FadeInAsync(FadeInMs);
    }

    /// Fades the window out and closes it.
    public async Task DismissAsync()
    {
        if (_isClosing) return;
        _isClosing = true;
        await FadeOutAsync(FadeOutMs);
        Close();
    }
}
