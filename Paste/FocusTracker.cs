using System.Runtime.InteropServices;
using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Models;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;

namespace Coworker.Paste;

/// The text field dictation will be pasted into: its accessibility element, the top-level window it lives
/// in and the app that owns it (Mac: DictationPasteController.Target).
internal sealed class FocusTarget : IPasteDestination
{
    public FocusTarget(AutomationElement element, IntPtr window, AppIdentity app)
    {
        Element = element;
        Window = window;
        App = app;
    }

    public AutomationElement Element { get; }
    public IntPtr Window { get; }
    public AppIdentity App { get; }
}

/// <summary>
/// Remembers the last editable field the user had focus in, in any other app, so a dictation started
/// from anywhere (even with Coworker in front) lands in the field they were typing in. Follows UI
/// Automation focus events, which also switches the accessibility tree of Chromium and Electron apps on.
/// Passwords are never remembered: a password field forgets the previous field as well. Mac:
/// DictationPasteController.captureFocus/frontmostFocus.
/// </summary>
internal sealed class FocusTracker
{
    public static FocusTracker Shared { get; } = new();

    private readonly UiaWorker _worker = new();
    private volatile FocusTarget? _last;
    private bool _started;

    private FocusTracker() { }

    public UiaWorker Worker => _worker;

    /// The last editable field outside this app, as of the last focus change.
    public FocusTarget? Last => _last;

    public void Start()
    {
        if (_started) return;
        _started = true;

        _worker.Post(() => _worker.Automation.RegisterFocusChangedEvent(element => _worker.Post(() => Remember(element))));
    }

    /// Reads the focus now and remembers it. Worker thread only.
    public FocusTarget? CaptureNow()
    {
        try
        {
            if (_worker.Automation.FocusedElement() is { } element) Remember(element);
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            DebugLog.Write($"Focus read failed: {ex.Message}");
        }
        return _last;
    }

    /// The field with focus right now, if it is an editable field outside this app. Worker thread only.
    public FocusTarget? Current()
    {
        try
        {
            return _worker.Automation.FocusedElement() is { } element && Describe(element) is { IsField: true } described
                ? described.Target
                : null;
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return null;
        }
    }

    /// The field with focus right now is a password field, in any app. Worker thread only.
    public bool IsPasswordFocused()
    {
        try
        {
            return _worker.Automation.FocusedElement() is { } element && UiaField.IsPassword(element);
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return false;
        }
    }

    private void Remember(AutomationElement element)
    {
        if (!AppSettings.Instance.DictationPasteToLastInput) return;

        try
        {
            var described = Describe(element);
            if (described.IsPassword) _last = null;   // a secure field invalidates the earlier field too
            else if (described.IsField) _last = described.Target;
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            // The element went away before it could be read.
        }
    }

    private readonly record struct Described(FocusTarget? Target, bool IsField, bool IsPassword);

    private Described Describe(AutomationElement element)
    {
        if (element.Properties.ProcessId.ValueOrDefault == Environment.ProcessId) return default;
        if (UiaField.IsPassword(element)) return new Described(null, false, true);
        if (!UiaField.AcceptsText(element)) return default;

        var window = UiaField.TopWindowOf(element);
        var app = window == IntPtr.Zero ? null : ForegroundApp.Describe(window);
        if (app == null || app.IsCoworker) return default;
        return new Described(new FocusTarget(element, app.Window, app), true, false);
    }

    internal static bool IsUiaFailure(Exception ex) =>
        ex is COMException or ElementNotAvailableException or FlaUIException or InvalidOperationException or NullReferenceException or TimeoutException;
}
