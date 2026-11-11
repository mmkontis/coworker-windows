using Microsoft.UI.Xaml.Controls;

namespace Coworker.Controls;

/// Placeholder body for tabs whose page has not been built yet.
public sealed partial class ComingSoonPanel : UserControl
{
    public ComingSoonPanel()
    {
        InitializeComponent();
    }

    public string Glyph
    {
        get => GlyphIcon.Glyph;
        set => GlyphIcon.Glyph = value;
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Message
    {
        get => MessageText.Text;
        set => MessageText.Text = value;
    }
}
