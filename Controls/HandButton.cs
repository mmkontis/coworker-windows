using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Coworker.Controls;

/// <summary>
/// A Button that shows the hand / pointer cursor on hover. WinUI has no public
/// Cursor property on FrameworkElement — the cursor is set via the protected
/// ProtectedCursor member, which is only reachable from a derived control.
/// </summary>
public class HandButton : Button
{
    public HandButton()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
