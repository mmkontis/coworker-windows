using Coworker.Helpers;
using Coworker.Transcription;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;

namespace Coworker.Paste;

/// What the paste engine reads from an accessibility element: whether it takes text, its contents and
/// its selection. Every call may block on the other app, so all of it runs on the <see cref="UiaWorker"/>.
internal static class UiaField
{
    // Browser and Electron pages are Documents too; only an editable field inside them takes text.
    private static readonly string[] WebFrameworks = { "Chrome", "Gecko", "Edge" };

    // The most text read from one field: a whole novel in Word is not worth waiting for.
    private const int MaxTextLength = 500_000;

    public static bool IsPassword(AutomationElement element) =>
        element.Properties.IsPassword.TryGetValue(out var isPassword) && isPassword;

    public static bool AcceptsText(AutomationElement element)
    {
        if (element.Properties.IsEnabled.TryGetValue(out var enabled) && !enabled) return false;

        var value = element.Patterns.Value.PatternOrDefault;
        var readOnly = value != null && value.IsReadOnly.TryGetValue(out var isReadOnly) && isReadOnly;

        var type = element.Properties.ControlType.ValueOrDefault;
        if (type is ControlType.Edit or ControlType.ComboBox) return !readOnly;
        if (value != null) return !readOnly;

        var framework = element.Properties.FrameworkId.ValueOrDefault ?? "";
        return type == ControlType.Document && element.Patterns.Text.IsSupported && !WebFrameworks.Contains(framework);
    }

    /// The top-level window that holds the element.
    public static IntPtr TopWindowOf(AutomationElement element)
    {
        var handle = IntPtr.Zero;
        var current = element;
        for (var depth = 0; depth < 64 && current != null && handle == IntPtr.Zero; depth++)
        {
            handle = current.Properties.NativeWindowHandle.ValueOrDefault;
            current = handle == IntPtr.Zero ? current.Parent : null;
        }
        return handle == IntPtr.Zero ? IntPtr.Zero : ProcessNative.GetAncestor(handle, ProcessNative.GA_ROOT);
    }

    /// The element still exists, is enabled and is not a password field.
    public static bool IsUsable(FocusTarget target)
    {
        try
        {
            return target.Element.Properties.ControlType.TryGetValue(out _) &&
                   !(target.Element.Properties.IsEnabled.TryGetValue(out var enabled) && !enabled) &&
                   !IsPassword(target.Element);
        }
        catch (Exception ex) when (FocusTracker.IsUiaFailure(ex))
        {
            return false;
        }
    }

    /// The field's text, or null when it cannot be read. The text pattern is preferred because the selection
    /// offsets come from it; plain edit controls answer with their value instead.
    public static string? ReadText(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Text.PatternOrDefault is { } text) return text.DocumentRange.GetText(MaxTextLength);
            return element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
        }
        catch (Exception ex) when (FocusTracker.IsUiaFailure(ex))
        {
            return null;
        }
    }

    /// The selection (or the caret, as an empty selection) as an offset and a length into <see cref="ReadText"/>.
    public static (int Start, int Length)? ReadSelection(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Text.PatternOrDefault is not { } text) return null;
            var selection = text.GetSelection();
            if (selection.Length != 1) return null;

            // Text before the selection: the document up to the point where the selection starts.
            var before = text.DocumentRange.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            return (before.GetText(MaxTextLength).Length, selection[0].GetText(MaxTextLength).Length);
        }
        catch (Exception ex) when (FocusTracker.IsUiaFailure(ex))
        {
            return null;
        }
    }

    /// What the field says about itself (control type, name, help text, id, ARIA properties), never its value,
    /// so the server can format the take for it (Transcription/TakeField.cs). A property that cannot be read is
    /// left out rather than losing the rest.
    public static UiaFieldFacts ReadFieldFacts(AutomationElement element)
    {
        static T? Read<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (FocusTracker.IsUiaFailure(ex)) { return default; }
        }

        var properties = element.Properties;
        var name = Read(() => properties.Name.ValueOrDefault);
        return new UiaFieldFacts(
            ControlType: Read(() => properties.ControlType.TryGetValue(out var type) ? type.ToString() : null),
            LocalizedControlType: Read(() => properties.LocalizedControlType.ValueOrDefault),
            Name: name,
            // A native form keeps the label in a separate element.
            LabeledByName: string.IsNullOrWhiteSpace(name) ? Read(() => properties.LabeledBy.ValueOrDefault?.Properties.Name.ValueOrDefault) : null,
            HelpText: Read(() => properties.HelpText.ValueOrDefault),
            FullDescription: Read(() => properties.FullDescription.ValueOrDefault),
            AutomationId: Read(() => properties.AutomationId.ValueOrDefault),
            AriaProperties: Read(() => properties.AriaProperties.ValueOrDefault),
            FrameworkId: Read(() => properties.FrameworkId.ValueOrDefault),
            IsPassword: Read(() => IsPassword(element)),
            Win32Multiline: Read(() => IsWin32Multiline(element)));
    }

    // Win32 edit controls (and WinForms text boxes) say whether they take several lines only in their window style.
    private static bool? IsWin32Multiline(AutomationElement element)
    {
        const uint ES_MULTILINE = 0x0004;
        var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == IntPtr.Zero) return null;

        var className = ProcessNative.ClassNameOf(handle);
        var isEdit = className.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                     className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                     className.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase);
        return isEdit ? (NativeMethods.GetWindowLong(handle, NativeMethods.GWL_STYLE) & ES_MULTILINE) != 0 : null;
    }

    public static bool SameElement(FlaUI.UIA3.UIA3Automation automation, AutomationElement a, AutomationElement b)
    {
        try { return automation.Compare(a, b); }
        catch (Exception ex) when (FocusTracker.IsUiaFailure(ex)) { return false; }
    }
}
