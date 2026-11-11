using Coworker.Helpers;
using Coworker.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Coworker.Settings;

/// <summary>
/// The shortcut chip of Settings: shows the current combination in monospace; a click starts
/// recording ("Press keys..." in blue). A modifier + key combination is taken as soon as the key goes
/// down; with <see cref="AllowModifierOnly"/>, holding modifiers alone and letting go records the hold
/// ("Ctrl", "Ctrl+Shift"). A combination that can't work (no modifier, or one every app owns such as
/// Ctrl+A) is explained in the chip and recording carries on. Esc or focus loss cancels
/// (Mac: VoiceHotkeyRecorderView / HotkeyRecorderView).
/// </summary>
public sealed class ShortcutRecorder : Button
{
    private readonly TextBlock _label = new()
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
    };

    private string _shortcut = "";
    private bool _recording;
    private ShortcutModifiers _peak;   // the most modifiers held together while recording
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hintTimer;

    public ShortcutRecorder()
    {
        SettingsResources.EnsureLoaded();
        DefaultStyleKey = typeof(Button);
        Content = _label;
        Padding = new Thickness(12, 6, 12, 6);
        MinWidth = 0;
        MinHeight = 0;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);

        Click += (_, _) => SetRecording(!_recording);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        LostFocus += (_, _) => SetRecording(false);
        Unloaded += (_, _) => SetRecording(false);
        ActualThemeChanged += (_, _) => UpdateVisual();
        UpdateVisual();
    }

    /// Raised for every recorder when one starts or stops listening, so the hotkey service can
    /// stand down: a global shortcut would otherwise fire instead of being recorded.
    public static event Action<bool>? RecordingChanged;

    /// The user recorded a new combination ("Ctrl+Shift+D", or "Ctrl+Shift" for a hold).
    public event Action<string>? ShortcutRecorded;

    /// Whether a hold of modifiers alone may be recorded. The dictation shortcut can be one; a shortcut
    /// registered with RegisterHotKey (the clipboard's) needs a key.
    public bool AllowModifierOnly { get; set; }

    public string Shortcut
    {
        get => _shortcut;
        set
        {
            _shortcut = value;
            UpdateVisual();
        }
    }

    private void SetRecording(bool recording)
    {
        if (_recording == recording) return;
        _recording = recording;
        _peak = ShortcutModifiers.None;
        _hintTimer?.Stop();
        UpdateVisual();
        RecordingChanged?.Invoke(recording);
    }

    private void UpdateVisual()
    {
        _label.Text = _recording ? (AllowModifierOnly ? "Press keys or hold modifiers..." : "Press keys...")
            : _shortcut.Length > 0 ? _shortcut : "Click to set";
        AutomationProperties.SetHelpText(this, _recording ? "Recording" : _shortcut);

        var accent = (Brush)Application.Current.Resources["CwAccentBrush"];
        var fill = _recording ? (Brush)Application.Current.Resources["SettingsAccentFill"] : (Brush)Application.Current.Resources["SettingsRecorderFill"];
        var stroke = _recording ? accent : (Brush)Application.Current.Resources["SettingsRecorderStroke"];

        _label.Foreground = _recording ? accent : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        Background = fill;
        BorderBrush = stroke;
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            Resources["ButtonBackground" + state] = fill;
            Resources["ButtonBorderBrush" + state] = stroke;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            SetRecording(false);
            return;
        }

        var held = HeldModifiers();
        _peak |= held;
        var vk = (uint)e.Key;
        if (HotkeyShortcut.IsModifierKey(vk)) return;
        if (HotkeyShortcut.KeyName(vk) == null) return;   // a key a shortcut can't use (media keys and the like)

        var candidate = new HotkeyShortcut(held, vk);
        if (held == ShortcutModifiers.None)
        {
            ShowHint("Add Ctrl, Alt, Shift or Win");
            return;
        }
        if (!candidate.IsValid)
        {
            ShowHint($"{candidate} is used by every app");
            return;
        }
        Commit(candidate.ToString());
    }

    // Letting go of the modifiers without pressing a key records the hold itself.
    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        if (!AllowModifierOnly || !HotkeyShortcut.IsModifierKey((uint)e.Key)) return;
        if (HeldModifiers() != ShortcutModifiers.None || _peak == ShortcutModifiers.None) return;

        Commit(new HotkeyShortcut(_peak, 0).ToString());
    }

    private void Commit(string shortcut)
    {
        _shortcut = shortcut;
        SetRecording(false);
        ShortcutRecorded?.Invoke(_shortcut);
    }

    /// Shows a short note in the chip for two seconds (a host refusing a shortcut, say), then the shortcut again.
    public void ShowMessage(string text) => ShowHint(text);

    // Says why a combination was not taken, then goes back to listening.
    private void ShowHint(string text)
    {
        _peak = ShortcutModifiers.None;
        _label.Text = text;
        _hintTimer ??= CreateHintTimer();
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateHintTimer()
    {
        var timer = UiThread.Queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => UpdateVisual();
        return timer;
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
