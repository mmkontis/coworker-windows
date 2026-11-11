using System.Text.RegularExpressions;
using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Transcription;
using FlaUI.Core.AutomationElements;

namespace Coworker.Paste;

/// <summary>
/// Puts dictated text into the field the user was dictating into and, when asked, presses Return
/// (Mac: DictationPasteController). Windows has no way to set only the selection of another app's field
/// (a value pattern replaces everything), so the text goes in through the clipboard and Ctrl+V, with the
/// same safety net around it as the Mac's:
///   - the destination is the last editable field outside this app, decided when the words arrive;
///   - never a password field, never an app running as administrator (Windows blocks the keys);
///   - focus is restored to that field first, and the paste is verified by reading the field back;
///   - a paste that provably changed nothing is tried once more, then reported;
///   - Return goes out only after a confirmed paste into the field that still has focus, so text that
///     could not be checked is never submitted.
/// The text is always left on the clipboard. Pastes run one at a time.
/// </summary>
public sealed class PasteEngine : IDictationPaster
{
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ModifierTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ReturnEffectTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PasteSpacing = TimeSpan.FromMilliseconds(200);
    private const int MaxRetryLength = 1_500;   // longer pastes can become attachments without changing the text

    // Terminals take Shift+Insert; the remote desktop client would paste on the remote machine's clipboard.
    private static readonly HashSet<string> ShiftInsertWindows = new() { "ConsoleWindowClass", "mintty", "PuTTY", "VirtualConsoleClass" };
    private static readonly HashSet<string> ClipboardOnlyWindows = new() { "TscShellContainerClass" };

    private static readonly bool IsElevatedSelf = IsRunningElevated();

    private readonly Func<bool> _allowsKeyboard;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly FocusTracker _tracker = FocusTracker.Shared;

    /// <param name="allowsKeyboardEvents">False while a take is being recorded: keys sent now would end it.</param>
    public PasteEngine(Func<bool> allowsKeyboardEvents) => _allowsKeyboard = allowsKeyboardEvents;

    /// The engine the dictation controller pastes with.
    public static PasteEngine Shared { get; } = new(() => !DictationController.Shared.IsCaptureActive);

    /// Raised on the UI thread after a paste that the user did not have Auto-Enter for, with the field it went into.
    internal event Action<FocusTarget>? ManualPasteConfirmed;

    public IPasteDestination? CaptureDestination() =>
        AppSettings.Instance.DictationPasteToLastInput ? _tracker.Last : null;

    public async Task<PasteResult> PasteAsync(string text, IPasteDestination? destination)
    {
        // Read on the calling (UI) thread: the dictionary cache is UI-thread only.
        var fit = new FitOptions(DictionaryCache.Words(), AppSettings.Instance.SpaceAfterSentenceEnd);
        await _serial.WaitAsync();
        try
        {
            var result = await _tracker.Worker.Run(() => PasteOnWorker(text, destination as FocusTarget, fit));
            // Give an app that has not confirmed the paste time to read the clipboard before the next one overwrites it.
            if (result.Method.EndsWith("_unverified", StringComparison.Ordinal) || result.Outcome == PasteOutcome.Unconfirmed)
                await Task.Delay(PasteSpacing);
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    /// The field the take starting now will most likely land in, found the way <see cref="PasteOnWorker"/> finds
    /// it (Mac: DictationPasteController.fieldDescriptor()). Only what the field says about itself is read, never
    /// its value; a password field gives nothing.
    public Task<TakeField?> ReadTakeFieldAsync() => _tracker.Worker.Run<TakeField?>(() =>
    {
        try
        {
            var target = AppSettings.Instance.DictationPasteToLastInput ? _tracker.CaptureNow() : null;
            if (target == null || !UiaField.IsUsable(target)) target = _tracker.Current();
            if (target == null) return null;

            var field = TakeFieldDescriptor.From(UiaField.ReadFieldFacts(target.Element));
            if (field != null) DebugLog.Write($"Take field: {field.Summary()}");
            return field;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Take field not read: {ex.Message}");
            return null;
        }
    });

    // ===== Worker thread =====

    /// What fitting the text to the field needs, captured on the UI thread.
    private sealed record FitOptions(IReadOnlyList<string> DictionaryWords, bool SpaceAfterSentenceEnd);

    private PasteResult PasteOnWorker(string text, FocusTarget? remembered, FitOptions fit)
    {
        var rememberMode = AppSettings.Instance.DictationPasteToLastInput;

        // The dictated text goes on the clipboard first; everything after this is about getting it into the field.
        if (!ClipboardWriter.SetText(text)) DebugLog.Write("Clipboard could not be opened; the paste may fail");

        FocusTarget? target;
        if (rememberMode)
        {
            var latest = _tracker.CaptureNow();
            target = remembered ?? latest;
            // The remembered field is gone when its composer re-rendered, e.g. after a message was sent.
            // The field that has focus now is the destination.
            if (target == null || !UiaField.IsUsable(target)) target = _tracker.Current();
        }
        else
        {
            target = _tracker.Current();
        }

        if (target == null)
        {
            // Keys for an app with no readable fields still go to it, but never into a password field.
            return _tracker.IsPasswordFocused()
                ? ClipboardOnly(ForegroundApp.Current()?.DisplayName, "clipboard_only_secure")
                : PasteIntoForeground();
        }

        var app = target.App;
        if (!IsElevatedSelf && ProcessNative.IsElevated(app.ProcessId)) return new PasteResult(PasteOutcome.ElevatedTarget, app.DisplayName, "clipboard_only_elevated");
        if (ClipboardOnlyWindows.Contains(ProcessNative.ClassNameOf(target.Window))) return ClipboardOnly(app.DisplayName, "clipboard_only");

        var site = BrowserSite.IsBrowser(app.ProcessName) ? BrowserSite.HostOfWindow(_tracker.Worker.Automation, target.Window) : null;
        var autoSubmit = AutoEnterRules.ShouldAutoSubmit(app.DisplayName, site);

        return PasteWithKeyboard(text, target, restoreFocus: rememberMode, autoSubmit, retried: false, fit);
    }

    private PasteResult PasteWithKeyboard(string text, FocusTarget target, bool restoreFocus, bool autoSubmit, bool retried, FitOptions? fit)
    {
        var app = target.App.DisplayName;
        if (!_allowsKeyboard()) return Deferred(app);
        if (restoreFocus && !RestoreFocus(target)) return _allowsKeyboard() ? ClipboardOnly(app, "clipboard_only") : Deferred(app);

        // Dictation fits where it lands (Mac: DictationPasteController.fitted): mid-sentence it continues in
        // lowercase, right after ". ! ?" it starts with a space. Once, before the first attempt; a retry pastes
        // the same fitted text.
        if (!retried && fit != null)
        {
            var fitted = FitToField(text, target, fit);
            if (fitted != text)
            {
                text = fitted;
                if (!ClipboardWriter.SetText(text)) DebugLog.Write("Clipboard could not be opened for the fitted text");
            }
        }

        // Something copied since the first attempt must not be pasted instead.
        if (retried && ClipboardWriter.GetText() != text) ClipboardWriter.SetText(text);

        // Read the receipt at the point of insertion: activating a window can change the field's value and selection.
        var receipt = PredictReceipt(target, text);
        if (!IsFocused(target)) return ClipboardOnly(app, "clipboard_only");
        WaitForModifiersReleased();
        if (!_allowsKeyboard()) return Deferred(app);
        if (!SendPaste(target)) return ClipboardOnly(app, "clipboard_only");

        var method = retried ? "ctrl_v_retry" : "ctrl_v_focused";
        switch (WaitForReceipt(target, receipt))
        {
            case ReceiptResult.Confirmed confirmed:
                if (autoSubmit && _allowsKeyboard() && SubmitIfStillFocused(target))
                {
                    WaitForReturnEffect(target, confirmed.Text);
                    return new PasteResult(PasteOutcome.Inserted, app, method + "+return", AutoSubmitted: true);
                }
                if (!autoSubmit) UiThread.Post(() => ManualPasteConfirmed?.Invoke(target));
                return new PasteResult(PasteOutcome.Inserted, app, method);

            case ReceiptResult.Unverifiable:
                // Most likely in, but never submit text that cannot be seen.
                return new PasteResult(PasteOutcome.Inserted, app, method + "_unverified");

            case ReceiptResult.Unchanged when !retried && text.Length <= MaxRetryLength:
                // The field provably did not change, so a second paste cannot duplicate anything.
                DebugLog.Write($"{app} left the field unchanged; pasting again");
                return PasteWithKeyboard(text, target, restoreFocus, autoSubmit, retried: true, fit: null);

            default:
                return new PasteResult(PasteOutcome.Unconfirmed, app, method + "_unconfirmed");
        }
    }

    // The field's text around the caret decides the fit. A field that can't be read (no text pattern, or no
    // single selection) gets the text unchanged, and so does a caret at the very start.
    private static string FitToField(string text, FocusTarget target, FitOptions fit)
    {
        var value = UiaField.ReadText(target.Element);
        var selection = UiaField.ReadSelection(target.Element);
        if (value == null || selection is not { } range || range.Start <= 0 || range.Start > value.Length) return text;

        var length = Math.Min(range.Length, value.Length - range.Start);
        try
        {
            return DictationTextRules.Fitted(
                text,
                before: value.Substring(0, range.Start),
                selected: value.Substring(range.Start, length),
                after: value.Substring(range.Start + length),
                fit.DictionaryWords,
                fit.SpaceAfterSentenceEnd);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            DebugLog.Write($"Text not fitted to the field: {ex.Message}");
            return text;
        }
    }

    private static PasteResult ClipboardOnly(string? app, string method) => new(PasteOutcome.ClipboardOnly, app, method);

    private static PasteResult Deferred(string? app) => new(PasteOutcome.Deferred, app, "deferred_capture");

    // No field this app can recognize has focus. Apps with poor accessibility (custom-drawn interfaces,
    // remote sessions) still take Ctrl+V, so the keys go to whatever is in front, unverified and never
    // followed by Return.
    private PasteResult PasteIntoForeground()
    {
        var front = ForegroundApp.Current();
        if (!_allowsKeyboard()) return Deferred(front?.DisplayName);
        if (front == null || front.IsCoworker) return ClipboardOnly(front?.DisplayName, "clipboard_only");
        if (!IsElevatedSelf && ProcessNative.IsElevated(front.ProcessId)) return new PasteResult(PasteOutcome.ElevatedTarget, front.DisplayName, "clipboard_only_elevated");

        var className = ProcessNative.ClassNameOf(front.Window);
        if (ClipboardOnlyWindows.Contains(className)) return ClipboardOnly(front.DisplayName, "clipboard_only");

        WaitForModifiersReleased();
        if (!_allowsKeyboard()) return Deferred(front.DisplayName);
        if (!SendPaste(className)) return ClipboardOnly(front.DisplayName, "clipboard_only");
        return new PasteResult(PasteOutcome.Inserted, front.DisplayName, "ctrl_v_unverified");
    }

    // ===== Receipt =====

    private abstract record ReceiptResult
    {
        /// The words are in the field; carries the text that proved it.
        public sealed record Confirmed(string Text) : ReceiptResult;

        /// The field is unreadable or changed some other way: probably in, unprovable.
        public sealed record Unverifiable : ReceiptResult;

        /// The field still holds exactly what it held before: nothing went in.
        public sealed record Unchanged : ReceiptResult;
    }

    private static PasteReceipt? PredictReceipt(FocusTarget target, string text)
    {
        var before = UiaField.ReadText(target.Element);
        var selection = UiaField.ReadSelection(target.Element);
        return PasteReceipt.Predict(before, selection?.Start, selection?.Length ?? 0, text);
    }

    private ReceiptResult WaitForReceipt(FocusTarget target, PasteReceipt? receipt)
    {
        if (receipt == null) return new ReceiptResult.Unverifiable();

        var deadline = Environment.TickCount64 + (long)ReceiptTimeout.TotalMilliseconds;
        string? lastSeen = null;
        while (true)
        {
            var actual = UiaField.ReadText(target.Element);
            // An unchanged value cannot newly prove the paste, unless the caret is the proof.
            if (actual != null && (actual != lastSeen || receipt.CaretProves) && receipt.Proves(actual, receipt.CaretProves ? CaretOf(target) : null))
                return new ReceiptResult.Confirmed(actual);

            if (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
                lastSeen = actual;
                continue;
            }

            // Only proof of failure when a successful paste would have changed the text.
            return actual != null && receipt.IsUnchanged(actual) ? new ReceiptResult.Unchanged() : new ReceiptResult.Unverifiable();
        }
    }

    private static int? CaretOf(FocusTarget target)
    {
        var selection = UiaField.ReadSelection(target.Element);
        return selection is { Length: 0 } ? selection.Value.Start : null;
    }

    // ===== Focus =====

    private bool RestoreFocus(FocusTarget target)
    {
        var deadline = Environment.TickCount64 + (long)FocusTimeout.TotalMilliseconds;
        while (_allowsKeyboard() && UiaField.IsUsable(target))
        {
            if (IsFocused(target)) return true;
            if (Environment.TickCount64 >= deadline) return false;

            // Window activation runs asynchronously: yield, then retry the stage that is still missing.
            if (TopWindowNow() != target.Window) ProcessNative.BringToFront(target.Window);
            else TryFocus(target.Element);
            Thread.Sleep(20);
        }
        return false;
    }

    private bool IsFocused(FocusTarget target)
    {
        if (TopWindowNow() != target.Window) return false;
        try
        {
            return _tracker.Worker.Automation.FocusedElement() is { } focused &&
                   UiaField.SameElement(_tracker.Worker.Automation, focused, target.Element);
        }
        catch (Exception ex) when (FocusTracker.IsUiaFailure(ex))
        {
            return false;
        }
    }

    private static IntPtr TopWindowNow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == IntPtr.Zero ? IntPtr.Zero : ProcessNative.GetAncestor(foreground, ProcessNative.GA_ROOT);
    }

    private static void TryFocus(AutomationElement element)
    {
        try { element.Focus(); }
        catch (Exception ex) when (FocusTracker.IsUiaFailure(ex)) { }
    }

    // ===== Keys =====

    // Keys sent while the user still holds a modifier would arrive as a different shortcut (Ctrl+Shift+V).
    private static void WaitForModifiersReleased()
    {
        var deadline = Environment.TickCount64 + (long)ModifierTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline && AnyModifierDown()) Thread.Sleep(10);
    }

    private static bool AnyModifierDown() =>
        InputNative.IsDown(InputNative.VK_CONTROL) || InputNative.IsDown(InputNative.VK_SHIFT) || InputNative.IsDown(InputNative.VK_MENU) ||
        InputNative.IsDown(InputNative.VK_LWIN) || InputNative.IsDown(InputNative.VK_RWIN);

    private static bool SendPaste(FocusTarget target) => SendPaste(ProcessNative.ClassNameOf(target.Window));

    private static bool SendPaste(string windowClass) =>
        ShiftInsertWindows.Contains(windowClass)
            ? InputNative.Send((InputNative.VK_SHIFT, false), (InputNative.VK_INSERT, false), (InputNative.VK_INSERT, true), (InputNative.VK_SHIFT, true))
            : InputNative.Send((InputNative.VK_CONTROL, false), (InputNative.VK_V, false), (InputNative.VK_V, true), (InputNative.VK_CONTROL, true));

    /// Return is only allowed while the exact recipient still has focus.
    private bool SubmitIfStillFocused(FocusTarget target) =>
        IsFocused(target) && InputNative.Send((InputNative.VK_RETURN, false), (InputNative.VK_RETURN, true));

    // A following paste must not overtake the Return: wait for its effect on the field, briefly.
    private static void WaitForReturnEffect(FocusTarget target, string baseline)
    {
        var deadline = Environment.TickCount64 + (long)ReturnEffectTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var actual = UiaField.ReadText(target.Element);
            if (actual != null && PasteReceipt.Normalized(actual) != PasteReceipt.Normalized(baseline)) return;
            Thread.Sleep(2);
        }
    }

    private static bool IsRunningElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
