using System.Runtime.InteropServices;

namespace Coworker.E2E;

/// <summary>
/// Real key presses through SendInput, the same path a keyboard driver's input takes. The dictation shortcut is
/// tagged with <see cref="TestKeyMarker"/> so the app (started with COWORKER_E2E=1) treats it as a real key; the
/// app ignores every other injected key on purpose, see Helpers\EndToEndTestMode.cs in the app.
/// </summary>
internal static class Keyboard
{
    /// Must match EndToEndTestMode.KeyMarker in the app.
    public const ulong TestKeyMarker = 0xC0E2E;

    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_END = 0x23;
    public const ushort VK_DELETE = 0x2E;
    public const ushort VK_A = 0x41;

    private const ushort ScanLeftControl = 0x1D;
    private const ushort ScanLeftShift = 0x2A;

    /// Holds the dictation shortcut (Ctrl+Shift) the way a person does: Ctrl, then Shift a moment later.
    public static void HoldDictationShortcut()
    {
        Send(Key(VK_LCONTROL, ScanLeftControl, up: false, tagged: true));
        Thread.Sleep(60);
        Send(Key(VK_LSHIFT, ScanLeftShift, up: false, tagged: true));
    }

    public static void ReleaseDictationShortcut()
    {
        Send(Key(VK_LSHIFT, ScanLeftShift, up: true, tagged: true));
        Thread.Sleep(40);
        Send(Key(VK_LCONTROL, ScanLeftControl, up: true, tagged: true));
    }

    /// A shortcut such as Ctrl+A, untagged (an ordinary injected key the app must ignore).
    public static void Chord(ushort modifier, ushort key)
    {
        Send(Key(modifier, 0, up: false), Key(key, 0, up: false), Key(key, 0, up: true), Key(modifier, 0, up: true));
        Thread.Sleep(80);
    }

    public static void Press(ushort key)
    {
        Send(Key(key, 0, up: false), Key(key, 0, up: true));
        Thread.Sleep(50);
    }

    /// Types text character by character (KEYEVENTF_UNICODE), as a person would.
    public static void Type(string text)
    {
        foreach (var unit in text)
        {
            Send(Unicode(unit, up: false), Unicode(unit, up: true));
            Thread.Sleep(15);
        }
    }

    private static Input Key(ushort vk, ushort scan, bool up, bool tagged = false) => new()
    {
        Type = InputKeyboard,
        U = new InputUnion
        {
            Ki = new KeyboardInput
            {
                Vk = vk,
                Scan = scan,
                Flags = (up ? KeyEventKeyUp : 0) | (IsExtended(vk) ? KeyEventExtendedKey : 0),
                ExtraInfo = tagged ? unchecked((IntPtr)(long)TestKeyMarker) : IntPtr.Zero,
            },
        },
    };

    private static Input Unicode(char unit, bool up) => new()
    {
        Type = InputKeyboard,
        U = new InputUnion { Ki = new KeyboardInput { Scan = unit, Flags = KeyEventUnicode | (up ? KeyEventKeyUp : 0) } },
    };

    private static bool IsExtended(ushort vk) => vk is VK_END or VK_DELETE or 0x24 /* Home */ or 0x21 or 0x22 or 0x25 or 0x26 or 0x27 or 0x28;

    private static void Send(params Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events (error {Marshal.GetLastWin32Error()})");
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    // The union is as large as its largest member (MOUSEINPUT), which SendInput's cbSize checks.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
