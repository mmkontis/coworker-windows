using Coworker.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Coworker.Controls;

/// <summary>
/// A button that records a shortcut: click it, then press the combination (Mac: the shortcut recorder
/// in Settings and the sidebar card). A key needs at least one modifier (a bare key would take that
/// key away from every app), Escape cancels, and holding modifiers alone records a hold such as
/// "Ctrl+Shift". The global hotkeys are suspended while it listens, so pressing the current shortcut
/// records it instead of starting a dictation.
/// </summary>
public sealed class ShortcutRecorderButton : Button
{
    private readonly TextBlock _label = new()
    {
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        FontFamily = new FontFamily("Consolas"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private string _shortcut;
    private bool _recording;
    private ShortcutModifiers _peak;   // the most modifiers held together while recording

    /// <param name="shortcut">The stored shortcut text to show.</param>
    /// <param name="allowModifierOnly">Whether "Ctrl+Shift" (no key) may be recorded; a shortcut registered with RegisterHotKey needs a key.</param>
    public ShortcutRecorderButton(string shortcut, bool allowModifierOnly, string automationName)
    {
        _shortcut = shortcut;
        AllowModifierOnly = allowModifierOnly;

        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14, 8, 14, 8);
        MinWidth = 140;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Content = _label;
        _label.Text = shortcut;
        AutomationProperties.SetName(this, automationName);

        Click += (_, _) => (_recording ? (Action)Cancel : Begin)();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += (_, _) => Cancel();
        Unloaded += (_, _) => Cancel();
    }

    public bool AllowModifierOnly { get; }

    /// The recorded shortcut (stored text form).
    public string Shortcut
    {
        get => _shortcut;
        set
        {
            _shortcut = value;
            if (!_recording) _label.Text = value;
        }
    }

    /// A new shortcut was recorded.
    public event Action<string>? Recorded;

    private void Begin()
    {
        _recording = true;
        _peak = ShortcutModifiers.None;
        GlobalHotkeyService.Instance?.Suspend();

        _label.Text = "Press keys...";
        var accent = (Brush)Application.Current.Resources["CwAccentBrush"];
        _label.Foreground = accent;
        BorderBrush = accent;
        BorderThickness = new Thickness(1.5);
        Focus(FocusState.Programmatic);
    }

    private void Cancel() => Finish(null);

    private void Finish(string? recorded)
    {
        if (!_recording) return;
        _recording = false;
        GlobalHotkeyService.Instance?.Resume();

        _label.Foreground = null;
        BorderBrush = null;
        BorderThickness = new Thickness(0);
        if (recorded != null && recorded != _shortcut)
        {
            _shortcut = recorded;
            Recorded?.Invoke(recorded);
        }
        _label.Text = _shortcut;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            Cancel();
            return;
        }

        var held = HeldModifiers();
        _peak |= held;
        if (HotkeyShortcut.IsModifierKey((uint)e.Key)) return;

        // A key with no modifier is swallowed: it would become a global hotkey for that key.
        var candidate = new HotkeyShortcut(held, (uint)e.Key);
        if (held != ShortcutModifiers.None && candidate.IsValid && HotkeyShortcut.KeyName(candidate.Vk) != null)
            Finish(candidate.ToString());
    }

    // Releasing the modifiers without pressing a key records the hold itself.
    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording || !AllowModifierOnly || !HotkeyShortcut.IsModifierKey((uint)e.Key)) return;

        e.Handled = true;
        if (HeldModifiers() == ShortcutModifiers.None && _peak != ShortcutModifiers.None)
            Finish(new HotkeyShortcut(_peak, 0).ToString());
    }

    private static ShortcutModifiers HeldModifiers()
    {
        var mask = ShortcutModifiers.None;
        if (IsDown(VirtualKey.Control)) mask |= ShortcutModifiers.Ctrl;
        if (IsDown(VirtualKey.Shift)) mask |= ShortcutModifiers.Shift;
        if (IsDown(VirtualKey.Menu)) mask |= ShortcutModifiers.Alt;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mask |= ShortcutModifiers.Win;
        return mask;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
