using System.Runtime.InteropServices;
using Coworker.Controls;
using Coworker.Helpers;
using Coworker.Models;
using Microsoft.UI.Xaml;

namespace Coworker.Views;

/// The window around <see cref="CommandInputCard"/>. The recording bar never takes focus, so the
/// inline context input lives in its own window that does; it sits where the bar was and hands
/// the focus back to the app being dictated into when it closes.
public sealed class CommandInputWindow : FloatingWindow
{
    private const int WidthDip = 372;
    private const int HeightDip = 36;
    private const int FocusDelayMs = 100;
    private const int FadeInMs = 250;

    private readonly CommandInputCard _card;
    private readonly IntPtr _previousWindow;
    private bool _isClosing;

    /// Raised with the trimmed text after the window closed.
    public event Action<string>? Submitted;

    public CommandInputWindow(VoiceCommand command, IntPtr previousWindow)
        : base(new FloatingWindowOptions { NoActivate = false, CornerRadius = 15 })
    {
        _previousWindow = previousWindow;
        _card = new CommandInputCard(command);
        _card.Submitted += text => Finish(text);
        _card.Cancelled += () => Finish(null);
        Content = _card;

        var (bounds, scale) = CommandBarPlacement.Compute(previousWindow, WidthDip, HeightDip);
        Place(bounds, scale);
    }

    public async void ShowAndFocus()
    {
        SetOpacity(0);
        Present();
        BringToFront();
        var fade = FadeInAsync(FadeInMs);

        await Task.Delay(FocusDelayMs);
        if (!_isClosing) _card.FocusInput();
        await fade;
    }

    private void Finish(string? text)
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();

        // Back to the app the user was in before the action (which may open its own window) runs.
        if (_previousWindow != IntPtr.Zero) NativeMethods.SetForegroundWindow(_previousWindow);
        if (text != null) Submitted?.Invoke(text);
    }

    // Windows only lets the foreground thread hand out the foreground, so borrow its input queue.
    private void BringToFront()
    {
        var current = GetCurrentThreadId();
        var owner = GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var attached = owner != 0 && owner != current && AttachThreadInput(current, owner, true);
        NativeMethods.SetForegroundWindow(Hwnd);
        if (attached) AttachThreadInput(current, owner, false);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThread, uint toThread, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
