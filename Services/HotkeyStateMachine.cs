namespace Coworker.Services;

public enum HotkeyAction
{
    /// The dictation shortcut went down: start recording.
    Start,

    /// The dictation shortcut was released: stop recording and transcribe.
    Stop,

    /// Escape while a take is in flight: drop it.
    Cancel,
}

public enum HotkeyTimer
{
    /// Modifier-only shortcut: how long the combo must be held before it counts as a hold.
    Debounce,

    /// A modifier changed during a take: wait, then re-read the keyboard before deciding it was released.
    Resolution,

    /// Backstop that notices a release the hook missed.
    Poll,
}

/// The keyboard as the state machine sees it. Implemented over Win32 by <see cref="GlobalHotkeyService"/>.
public interface IHotkeyPlatform
{
    /// The real (asynchronous) key state, which still reflects a release the hook did not see.
    bool IsKeyDown(uint vk);

    /// Starts a timer that keeps firing every <paramref name="milliseconds"/> until stopped.
    void StartTimer(HotkeyTimer timer, int milliseconds);

    void StopTimer(HotkeyTimer timer);
}

/// <summary>
/// Turns key events into dictation start/stop/cancel (Mac: HotkeyManager). Hold-to-talk only:
/// the shortcut going down starts a take and releasing it ends it. Not thread-safe by design: every
/// call happens on the hook thread, which is also where the platform's timers fire.
///
/// A modifier-only shortcut ("Ctrl+Shift") starts once exactly those modifiers have been held for
/// <see cref="DebounceMs"/> with no other key down, so chords such as Ctrl+Shift+T never start a take.
/// A single modifier ("Ctrl") waits <see cref="SingleModifierDebounceMs"/>, and a key pressed within
/// <see cref="ChordWindowMs"/> of the start turns the take back into the chord it was (a slow Ctrl+C):
/// the take is dropped and the key goes through.
/// It ends when a required modifier is released, judged after <see cref="ResolutionMs"/> so a stray
/// extra modifier cannot cut a take short. A key shortcut ("Ctrl+Shift+D") starts on the key going down
/// with exactly those modifiers and ends when the key is released; its key events are swallowed.
/// Injected keys (our own paste, other software) never change any state.
/// </summary>
public sealed class HotkeyStateMachine
{
    public const int DebounceMs = 130;
    public const int SingleModifierDebounceMs = 250;
    public const int ChordWindowMs = 1000;
    public const int ResolutionMs = 160;
    public const int PollMs = 60;

    private const uint VkEscape = 0x1B;

    private enum Active { None, ModifierOnly, KeyCombo }

    private readonly IHotkeyPlatform _platform;
    private readonly Action<HotkeyAction> _raise;
    private readonly Func<bool> _cancelArmed;

    private readonly HashSet<uint> _down = new();
    private uint _processing;   // the key whose event is being handled: the real key state does not include it yet
    private HotkeyShortcut _shortcut = HotkeyShortcut.None;
    private Active _active;
    private bool _debouncing;
    private bool _resolving;
    private int _pollMisses;
    private long _activatedAt;

    public HotkeyStateMachine(IHotkeyPlatform platform, Action<HotkeyAction> raise, Func<bool> cancelArmed)
    {
        _platform = platform;
        _raise = raise;
        _cancelArmed = cancelArmed;
    }

    public bool IsActive => _active != Active.None;

    /// A take started with the Windows key held. Releasing that key would open the Start menu, so the
    /// host masks its release and clears this flag.
    public bool WinKeyUsed { get; set; }

    /// Replaces the shortcut. A take that was running ends (a moved shortcut cannot be released any more).
    public void Configure(HotkeyShortcut shortcut)
    {
        Reset();
        _shortcut = shortcut.IsValid ? shortcut : HotkeyShortcut.None;
    }

    /// Forgets all key state and stops a running take.
    public void Reset()
    {
        var wasActive = _active != Active.None;
        Deactivate();
        CancelDebounce();
        _down.Clear();
        if (wasActive) _raise(HotkeyAction.Stop);
    }

    /// <returns>True when the key event must not reach the focused application.</returns>
    public bool OnKey(uint vk, bool isDown, bool isInjected)
    {
        if (isInjected) return false;

        _processing = vk;
        try
        {
            return OnKeyCore(vk, isDown);
        }
        finally
        {
            _processing = 0;
        }
    }

    private bool OnKeyCore(uint vk, bool isDown)
    {
        var isRepeat = isDown && !_down.Add(vk);
        if (!isDown) _down.Remove(vk);

        if (vk == VkEscape && isDown && !isRepeat && _cancelArmed())
        {
            Deactivate();
            CancelDebounce();
            _raise(HotkeyAction.Cancel);
            return false;   // Escape still reaches the app, like the Mac
        }

        if (_shortcut.Modifiers == ShortcutModifiers.None) return false;

        var isModifier = HotkeyShortcut.IsModifierKey(vk);
        if (_shortcut.IsModifierOnly)
        {
            if (isModifier) OnModifierChanged();
            else if (isDown)
            {
                CancelDebounce();   // a real key: this is a chord, not a hold
                if (_active == Active.ModifierOnly && !isRepeat && Environment.TickCount64 - _activatedAt < ChordWindowMs)
                {
                    Deactivate();
                    _raise(HotkeyAction.Cancel);
                }
            }
            return false;
        }

        if (isModifier || vk != _shortcut.Vk) return false;
        return isDown ? OnComboKeyDown(isRepeat) : OnComboKeyUp();
    }

    public void OnTimer(HotkeyTimer timer)
    {
        switch (timer)
        {
            case HotkeyTimer.Debounce:
                _platform.StopTimer(HotkeyTimer.Debounce);
                if (!_debouncing) return;
                _debouncing = false;
                if (_active == Active.None && HeldModifiers() == _shortcut.Modifiers && NoOtherKeyDown(0))
                    Activate(Active.ModifierOnly);
                break;

            case HotkeyTimer.Resolution:
                _platform.StopTimer(HotkeyTimer.Resolution);
                _resolving = false;
                if (_active == Active.ModifierOnly && !StillHeld(RealModifiers())) End();
                break;

            case HotkeyTimer.Poll:
                OnPoll();
                break;
        }
    }

    private void OnModifierChanged()
    {
        var held = HeldModifiers();
        if (_active == Active.ModifierOnly)
        {
            if (StillHeld(held)) CancelResolution();
            else if (!_resolving)
            {
                _resolving = true;
                _platform.StartTimer(HotkeyTimer.Resolution, ResolutionMs);
            }
            return;
        }

        if (held == _shortcut.Modifiers && NoOtherKeyDown(0))
        {
            if (_debouncing) return;
            _debouncing = true;
            var single = System.Numerics.BitOperations.PopCount((uint)_shortcut.Modifiers) == 1;
            _platform.StartTimer(HotkeyTimer.Debounce, single ? SingleModifierDebounceMs : DebounceMs);
        }
        else
        {
            CancelDebounce();
        }
    }

    private bool OnComboKeyDown(bool isRepeat)
    {
        if (_active == Active.KeyCombo) return true;   // auto-repeat of the held key
        if (isRepeat || HeldModifiers() != _shortcut.Modifiers || !NoOtherKeyDown(_shortcut.Vk)) return false;

        Activate(Active.KeyCombo);
        return true;
    }

    private bool OnComboKeyUp()
    {
        if (_active != Active.KeyCombo) return false;
        End();
        return true;
    }

    private void OnPoll()
    {
        var released = _active switch
        {
            Active.ModifierOnly => !_resolving && !StillHeld(RealModifiers()),
            // The combo's key events are swallowed, so the real state never shows that key down: polling
            // it ended every take ~120 ms in. Its key-up still reaches the hook; the backstop watches the
            // modifiers, which pass through.
            Active.KeyCombo => !StillHeld(RealModifiers()),
            _ => false,
        };

        if (_active == Active.None) { _platform.StopTimer(HotkeyTimer.Poll); return; }

        // Injected modifier events show in the real state for a moment: only two misses in a row count.
        _pollMisses = released ? _pollMisses + 1 : 0;
        if (_pollMisses >= 2) End();
    }

    private void Activate(Active kind)
    {
        _active = kind;
        _pollMisses = 0;
        _activatedAt = Environment.TickCount64;
        if (_shortcut.Modifiers.HasFlag(ShortcutModifiers.Win)) WinKeyUsed = true;
        _platform.StartTimer(HotkeyTimer.Poll, PollMs);
        _raise(HotkeyAction.Start);
    }

    private void End()
    {
        Deactivate();
        _raise(HotkeyAction.Stop);
    }

    private void Deactivate()
    {
        if (_active == Active.None) return;
        _active = Active.None;
        _resolving = false;
        _platform.StopTimer(HotkeyTimer.Poll);
        _platform.StopTimer(HotkeyTimer.Resolution);
    }

    private void CancelDebounce()
    {
        if (!_debouncing) return;
        _debouncing = false;
        _platform.StopTimer(HotkeyTimer.Debounce);
    }

    private void CancelResolution()
    {
        if (!_resolving) return;
        _resolving = false;
        _platform.StopTimer(HotkeyTimer.Resolution);
    }

    // Mac isStillHeld: every required modifier is present; extra modifiers on top do not matter.
    private bool StillHeld(ShortcutModifiers modifiers) =>
        _shortcut.Modifiers != ShortcutModifiers.None && (modifiers & _shortcut.Modifiers) == _shortcut.Modifiers;

    /// Modifiers currently down according to the events seen. Keys whose release was missed (a locked
    /// workstation swallows key-ups) are dropped first, using the real state.
    private ShortcutModifiers HeldModifiers()
    {
        _down.RemoveWhere(vk => vk != _processing && !_platform.IsKeyDown(vk));

        var mask = ShortcutModifiers.None;
        foreach (var vk in _down) mask |= HotkeyShortcut.ModifierOf(vk);
        return mask;
    }

    private bool NoOtherKeyDown(uint allowed) =>
        !_down.Any(vk => vk != allowed && !HotkeyShortcut.IsModifierKey(vk));

    private ShortcutModifiers RealModifiers()
    {
        var mask = ShortcutModifiers.None;
        if (_platform.IsKeyDown(0x11)) mask |= ShortcutModifiers.Ctrl;
        if (_platform.IsKeyDown(0x10)) mask |= ShortcutModifiers.Shift;
        if (_platform.IsKeyDown(0x12)) mask |= ShortcutModifiers.Alt;
        if (_platform.IsKeyDown(0x5B) || _platform.IsKeyDown(0x5C)) mask |= ShortcutModifiers.Win;
        return mask;
    }
}
