namespace Coworker.Helpers;

/// <summary>
/// The end-to-end test (tests\E2E, workflow coworker-windows-e2e.yml) presses the dictation shortcut with
/// SendInput, which Windows marks as injected, and the hotkey ignores injected keys on purpose (the app's own
/// paste must never start or end a take). With COWORKER_E2E=1 set for the process, a key event the test tags
/// with <see cref="KeyMarker"/> in dwExtraInfo counts as a real key. Nothing else changes: untagged injected
/// keys, including the app's own paste, are still ignored, and without the variable nothing changes at all.
/// </summary>
internal static class EndToEndTestMode
{
    /// Must match the harness (tests\E2E\Keyboard.cs).
    public const ulong KeyMarker = 0xC0E2E;

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("COWORKER_E2E") == "1";

    /// True for a key the end-to-end test pressed on purpose.
    public static bool IsTestKey(IntPtr extraInfo) => Enabled && unchecked((ulong)(long)extraInfo) == KeyMarker;
}
