namespace Coworker.Services;

/// A shortcut recorder is open (<paramref name="Active"/>) or closed. The global hotkey hook should
/// stand down while it is open, or pressing the current combination starts a dictation. Sent with
/// WeakReferenceMessenger.Default (Mac: HotkeyManagerSuspend / HotkeyManagerResume).
public sealed record HotkeyCaptureMessage(bool Active);
