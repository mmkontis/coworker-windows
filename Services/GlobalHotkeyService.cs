using System.Runtime.InteropServices;
using Coworker.Helpers;
using Coworker.Models;

namespace Coworker.Services;

/// A key or mouse press seen while <see cref="GlobalHotkeyService.SetObserver"/> is active.
public readonly record struct InputObservation(bool IsMouse, uint Vk, ShortcutModifiers Modifiers, long Timestamp);

/// <summary>
/// System-wide dictation shortcut, Escape-to-cancel and the clipboard hotkey (Mac: HotkeyManager).
/// Everything lives on one dedicated thread with its own message loop: the low-level keyboard hook, the
/// state machine and its timers, and the message window that owns the clipboard hotkey. The UI thread
/// may be busy (layout, navigation, network), and a low-level hook served late makes the whole
/// system's input lag. Events are raised on the UI thread.
/// </summary>
public sealed class GlobalHotkeyService : IHotkeyPlatform, IDisposable
{
    public static GlobalHotkeyService? Instance { get; private set; }

    private const int ClipboardHotkeyId = 1;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint CmdApply = InputNative.WM_APP + 1;
    private const uint CmdObserver = InputNative.WM_APP + 2;
    private const uint CmdQuit = InputNative.WM_APP + 3;
    private const string WindowClass = "CoworkerHotkeyWindow";

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly HotkeyStateMachine _machine;
    private readonly HotkeyStateMachine _chatMachine;   // the chat-completion shortcut, on its own timers
    private readonly InputNative.HookProc _keyboardProc;
    private readonly InputNative.HookProc _mouseProc;
    private readonly RawWindowNative.WndProc _windowProc;

    private Thread? _thread;
    private IntPtr _window;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private volatile int _suspendCount;
    private int _suspendGeneration;   // bumped by every Suspend/Resume, so the watchdog knows nothing moved
    private volatile bool _cancelArmed;
    private volatile Action<InputObservation>? _observer;

    public GlobalHotkeyService()
    {
        _machine = new HotkeyStateMachine(this, OnAction, () => _cancelArmed);
        // Escape is the dictation machine's job (it cancels whichever take is running), so never armed here.
        _chatMachine = new HotkeyStateMachine(new TimerOffsetPlatform(this, ChatTimerOffset), OnChatAction, () => false);
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _windowProc = WindowProc;
        Instance = this;

        _thread = new Thread(Run) { IsBackground = true, Name = "CoworkerHotkeys" };
        _thread.Start();
        _ready.Wait(2000);
    }

    /// The dictation shortcut went down (UI thread).
    public event Action? DictationKeyDown;

    /// The dictation shortcut was released (UI thread).
    public event Action? DictationKeyUp;

    /// Escape was pressed while a take is in flight (UI thread).
    public event Action? CancelPressed;

    /// The clipboard shortcut was pressed (UI thread).
    public event Action? ClipboardHotkeyPressed;

    /// The chat-completion shortcut went down (UI thread).
    public event Action? ChatKeyDown;

    /// The chat-completion shortcut was released (UI thread).
    public event Action? ChatKeyUp;

    /// Escape only cancels while a take is in flight; the dictation controller keeps this current.
    public bool CancelArmed
    {
        set => _cancelArmed = value;
    }

    /// Re-reads the shortcuts from <see cref="AppSettings"/> (after the user changed one).
    public void RegisterAllHotkeys() => Post(CmdApply);

    /// Stops all hotkeys while a shortcut is being recorded, so pressing the current shortcut records it
    /// instead of starting a take (Mac: hotkeyManagerSuspend). Calls nest; each needs a <see cref="Resume"/>.
    public void Suspend()
    {
        Interlocked.Increment(ref _suspendCount);
        var generation = Interlocked.Increment(ref _suspendGeneration);
        Post(CmdApply);

        // A recorder that never says it finished would leave every shortcut dead with no sign of it.
        // Nobody records a shortcut for a minute: after that, stand back up.
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ =>
        {
            if (Volatile.Read(ref _suspendGeneration) != generation || _suspendCount == 0) return;
            DebugLog.Write($"Hotkeys were still suspended after a minute ({_suspendCount}); resuming");
            Interlocked.Exchange(ref _suspendCount, 0);
            Post(CmdApply);
        }, TaskScheduler.Default);
    }

    public void Resume()
    {
        if (Interlocked.Decrement(ref _suspendCount) < 0) Interlocked.Exchange(ref _suspendCount, 0);
        Interlocked.Increment(ref _suspendGeneration);
        Post(CmdApply);
    }

    /// Reports every real (not injected) key press and mouse click to <paramref name="observer"/> on the UI
    /// thread until called again with null. Used for the short interval after a paste.
    public void SetObserver(Action<InputObservation>? observer)
    {
        _observer = observer;
        Post(CmdObserver);
    }

    public void Dispose()
    {
        if (Instance == this) Instance = null;
        var thread = _thread;
        if (thread == null) return;
        _thread = null;
        Post(CmdQuit);
        thread.Join(2000);
        GC.SuppressFinalize(this);
    }

    // ===== IHotkeyPlatform (hook thread) =====

    bool IHotkeyPlatform.IsKeyDown(uint vk) => InputNative.IsDown((int)vk);

    void IHotkeyPlatform.StartTimer(HotkeyTimer timer, int milliseconds) =>
        InputNative.SetTimer(_window, (UIntPtr)((int)timer + 1), (uint)milliseconds, null);

    void IHotkeyPlatform.StopTimer(HotkeyTimer timer) =>
        InputNative.KillTimer(_window, (UIntPtr)((int)timer + 1));

    // Timer ids 1-3 belong to the dictation machine, ChatTimerOffset + 1.. to the chat one.
    private const int ChatTimerOffset = 10;

    private sealed class TimerOffsetPlatform(GlobalHotkeyService host, int offset) : IHotkeyPlatform
    {
        public bool IsKeyDown(uint vk) => InputNative.IsDown((int)vk);
        public void StartTimer(HotkeyTimer timer, int milliseconds) =>
            InputNative.SetTimer(host._window, (UIntPtr)(offset + (int)timer + 1), (uint)milliseconds, null);
        public void StopTimer(HotkeyTimer timer) => InputNative.KillTimer(host._window, (UIntPtr)(offset + (int)timer + 1));
    }

    // ===== Hook thread =====

    private void Run()
    {
        var instance = InputNative.GetModuleHandle(null);
        var wndClass = new RawWindowNative.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<RawWindowNative.WNDCLASSEX>(),
            lpfnWndProc = _windowProc,
            hInstance = instance,
            lpszClassName = WindowClass,
        };
        RawWindowNative.RegisterClassEx(ref wndClass);
        _window = RawWindowNative.CreateWindowEx(0, WindowClass, "", 0, 0, 0, 0, 0, RawWindowNative.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);

        _keyboardHook = InputNative.SetWindowsHookEx(InputNative.WH_KEYBOARD_LL, _keyboardProc, instance, 0);
        DebugLog.Write($"Keyboard hook {(_keyboardHook == IntPtr.Zero ? "FAILED" : "installed")} (error {Marshal.GetLastWin32Error()})");
        Apply();
        _ready.Set();

        while (InputNative.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            InputNative.TranslateMessage(ref msg);
            InputNative.DispatchMessage(ref msg);
        }

        if (_mouseHook != IntPtr.Zero) InputNative.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) InputNative.UnhookWindowsHookEx(_keyboardHook);
        InputNative.UnregisterHotKey(_window, ClipboardHotkeyId);
        RawWindowNative.DestroyWindow(_window);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case InputNative.WM_HOTKEY when (int)wParam == ClipboardHotkeyId:
                UiThread.Post(() => ClipboardHotkeyPressed?.Invoke());
                return IntPtr.Zero;
            case InputNative.WM_TIMER:
                var id = (int)wParam;
                if (id > ChatTimerOffset) _chatMachine.OnTimer((HotkeyTimer)(id - ChatTimerOffset - 1));
                else _machine.OnTimer((HotkeyTimer)(id - 1));
                return IntPtr.Zero;
            case CmdApply:
                Apply();
                return IntPtr.Zero;
            case CmdObserver:
                ApplyObserver();
                return IntPtr.Zero;
            case CmdQuit:
                InputNative.PostMessage(IntPtr.Zero, (uint)InputNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
        }
        return RawWindowNative.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Apply()
    {
        InputNative.UnregisterHotKey(_window, ClipboardHotkeyId);

        if (_suspendCount > 0)
        {
            _machine.Configure(HotkeyShortcut.None);
            _chatMachine.Configure(HotkeyShortcut.None);
            DebugLog.Write($"Hotkeys suspended while a shortcut is recorded ({_suspendCount})");
            return;
        }

        var settings = AppSettings.Instance;
        // A stored shortcut nobody can use (no modifier, or Ctrl+A and the like) falls back to the default
        // instead of turning dictation off.
        HotkeyShortcut dictation;
        if (HotkeyShortcut.TryParse(settings.DictationShortcut, out var parsed) && parsed.IsValid)
        {
            dictation = parsed;
        }
        else
        {
            dictation = HotkeyShortcut.TryParse(AppSettings.DefaultDictationShortcut, out parsed) ? parsed : HotkeyShortcut.None;
            DebugLog.Write($"Dictation shortcut '{settings.DictationShortcut}' can't be used; using {dictation}");
        }
        _machine.Configure(dictation);

        // The chat shortcut: off when it can't be used or would collide with dictation's.
        var chat = HotkeyShortcut.TryParse(settings.ChatShortcut, out var chatParsed) && chatParsed.IsValid && chatParsed != dictation
            ? chatParsed
            : HotkeyShortcut.None;
        _chatMachine.Configure(chat);

        if (HotkeyShortcut.TryParse(settings.ClipboardShortcut, out var clipboard) && clipboard.Vk != 0 && clipboard.Modifiers != ShortcutModifiers.None &&
            !InputNative.RegisterHotKey(_window, ClipboardHotkeyId, (uint)clipboard.Modifiers | MOD_NOREPEAT, clipboard.Vk))
        {
            DebugLog.Write($"Clipboard shortcut {clipboard} is in use by another app (error {Marshal.GetLastWin32Error()})");
        }
        DebugLog.Write($"Hotkeys applied: dictation={dictation} chat={(chat == HotkeyShortcut.None ? "off" : chat)} clipboard={settings.ClipboardShortcut}");
    }

    private void ApplyObserver()
    {
        var wanted = _observer != null;
        if (wanted && _mouseHook == IntPtr.Zero)
            _mouseHook = InputNative.SetWindowsHookEx(InputNative.WH_MOUSE_LL, _mouseProc, InputNative.GetModuleHandle(null), 0);
        else if (!wanted && _mouseHook != IntPtr.Zero)
        {
            InputNative.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private void OnAction(HotkeyAction action)
    {
        DebugLog.Write($"Hotkey action: {action}");
        switch (action)
        {
            case HotkeyAction.Start:
                UiThread.Post(() => DictationKeyDown?.Invoke());
                break;
            case HotkeyAction.Stop:
                UiThread.Post(() => DictationKeyUp?.Invoke());
                break;
            case HotkeyAction.Cancel:
                UiThread.Post(() => CancelPressed?.Invoke());
                break;
        }
    }

    private void OnChatAction(HotkeyAction action)
    {
        DebugLog.Write($"Chat hotkey action: {action}");
        switch (action)
        {
            case HotkeyAction.Start:
                UiThread.Post(() => ChatKeyDown?.Invoke());
                break;
            case HotkeyAction.Stop:
                UiThread.Post(() => ChatKeyUp?.Invoke());
                break;
            case HotkeyAction.Cancel:   // a slow chord such as Ctrl+Win+E: drop the take it started
                UiThread.Post(() => CancelPressed?.Invoke());
                break;
        }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _suspendCount == 0)
        {
            try
            {
                var msg = (int)wParam;
                var vk = (uint)Marshal.ReadInt32(lParam, 0);
                var flags = (uint)Marshal.ReadInt32(lParam, 8);
                var isDown = msg is InputNative.WM_KEYDOWN or InputNative.WM_SYSKEYDOWN;
                var injected = (flags & InputNative.LLKHF_INJECTED) != 0;
                // KBDLLHOOKSTRUCT.dwExtraInfo sits at offset 16 on every architecture. Only the end-to-end
                // test's own key presses pass here, and only when it runs the app with COWORKER_E2E=1.
                if (injected && EndToEndTestMode.IsTestKey(Marshal.ReadIntPtr(lParam, 16))) injected = false;

                var swallowDictation = _machine.OnKey(vk, isDown, injected);
                var swallowChat = _chatMachine.OnKey(vk, isDown, injected);
                if (swallowDictation || swallowChat) return (IntPtr)1;
                if (!injected) Observe(false, vk, isDown);
                if (!isDown && !injected && vk is InputNative.VK_LWIN or InputNative.VK_RWIN && (_machine.WinKeyUsed || _chatMachine.WinKeyUsed))
                {
                    // A hold that used the Windows key would open the Start menu when it goes up. A key
                    // in between stops that, so swallow the release and send it again after the mask key.
                    _machine.WinKeyUsed = false;
                    _chatMachine.WinKeyUsed = false;
                    InputNative.Send((InputNative.VK_UNASSIGNED, false), (InputNative.VK_UNASSIGNED, true), ((ushort)vk, true));
                    return (IntPtr)1;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Keyboard hook error: {ex.Message}");
            }
        }
        return InputNative.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is InputNative.WM_LBUTTONDOWN or InputNative.WM_RBUTTONDOWN or InputNative.WM_MBUTTONDOWN or InputNative.WM_XBUTTONDOWN)
        {
            var injected = ((uint)Marshal.ReadInt32(lParam, 12) & InputNative.LLMHF_INJECTED) != 0;   // MSLLHOOKSTRUCT.flags
            if (!injected) Observe(true, 0, true);
        }
        return InputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void Observe(bool isMouse, uint vk, bool isDown)
    {
        var observer = _observer;
        if (observer == null || !isDown) return;

        var modifiers = ShortcutModifiers.None;
        if (InputNative.IsDown(InputNative.VK_CONTROL)) modifiers |= ShortcutModifiers.Ctrl;
        if (InputNative.IsDown(InputNative.VK_SHIFT)) modifiers |= ShortcutModifiers.Shift;
        if (InputNative.IsDown(InputNative.VK_MENU)) modifiers |= ShortcutModifiers.Alt;
        if (InputNative.IsDown(InputNative.VK_LWIN) || InputNative.IsDown(InputNative.VK_RWIN)) modifiers |= ShortcutModifiers.Win;

        var observation = new InputObservation(isMouse, vk, modifiers, Environment.TickCount64);
        UiThread.Post(() => observer(observation));
    }

    private void Post(uint command)
    {
        if (_window != IntPtr.Zero) InputNative.PostMessage(_window, command, IntPtr.Zero, IntPtr.Zero);
    }
}
