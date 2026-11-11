using Coworker.Models;
using Coworker.Services.VoiceCommands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Coworker.Controls;

/// The inline "Type context…" input (Mac: CommandWebInputView): the command's icon, a text box,
/// a run button and a close button, tinted with the command's color. Enter runs, Esc closes.
public sealed partial class CommandInputCard : UserControl
{
    private static readonly Color Panel = Color.FromArgb(0xE8, 0x0C, 0x14, 0x2B);
    private static readonly Brush DisabledDisc = new SolidColorBrush(Color.FromArgb(0x38, 0xEB, 0xEB, 0xF5));

    private readonly Brush _accent;

    public event Action<string>? Submitted;
    public event Action? Cancelled;

    public CommandInputCard(VoiceCommand command)
    {
        InitializeComponent();

        var color = CommandIcons.ParseColor(command.IconColor);
        _accent = new SolidColorBrush(color);
        CommandIcon.Glyph = CommandIcons.Glyph(command.IconName);
        CommandIcon.Foreground = _accent;
        Pill.Background = new SolidColorBrush(Tint(Panel, color, 0.08));
        Pill.BorderBrush = new SolidColorBrush(CommandIcons.WithOpacity(color, 0.30));
        SubmitDisc.Fill = DisabledDisc;
        AutomationProperties.SetName(this, $"Type context for {command.TriggerPhrase}");
    }

    public void FocusInput() => InputBox.Focus(FocusState.Programmatic);

    private void Submit()
    {
        var text = InputBox.Text.Trim();
        if (text.Length > 0) Submitted?.Invoke(text);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrWhiteSpace(InputBox.Text);
        SubmitButton.IsEnabled = hasText;
        SubmitDisc.Fill = hasText ? _accent : DisabledDisc;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                Submit();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Cancelled?.Invoke();
                break;
        }
    }

    private void OnSubmitClick(object sender, RoutedEventArgs e) => Submit();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private static Color Tint(Color baseColor, Color tint, double amount) => Color.FromArgb(
        baseColor.A,
        (byte)Math.Round(baseColor.R * (1 - amount) + tint.R * amount),
        (byte)Math.Round(baseColor.G * (1 - amount) + tint.G * amount),
        (byte)Math.Round(baseColor.B * (1 - amount) + tint.B * amount));
}
