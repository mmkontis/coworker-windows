using CommunityToolkit.Mvvm.Messaging;
using Coworker.Bar;
using Coworker.Models;
using Coworker.Paste;
using Coworker.Services;
using Coworker.Services.VoiceCommands;
using Coworker.Transcription;

namespace Coworker.Dictation;

/// <summary>
/// Starts everything dictation needs and wires it together (Mac: the recording setup in
/// AppDelegate.applicationDidFinishLaunching): the shortcut hook, the floating bar that shows the
/// controller's state, focus tracking, the dictionary cache and the remote defaults. Call
/// <see cref="Start"/> once from the UI thread after the main window exists.
/// </summary>
public sealed class DictationService
{
    public static DictationService Shared { get; } = new();

    private FloatingBar? _bar;
    private bool _started;

    private DictationService() { }

    /// The shortcut hook, for the clipboard shortcut and for shortcut recorders (Suspend/Resume).
    /// Available after <see cref="Start"/>.
    public GlobalHotkeyService Hotkeys { get; private set; } = null!;

    /// The floating bar's window right now, or null before <see cref="Start"/>. Anything that takes the
    /// pill's place for a moment, like a voice command's badge, is centred on it.
    public BarPlacement? BarLocation => _bar?.Placement;

    public void Start()
    {
        if (_started) return;
        _started = true;

        var controller = DictationController.Shared;
        DictionaryCache.Start();
        DictionaryCache.ExtraWordProviders.Add(VoiceCommandService.Shared.TriggerPhrasesForPriming);
        controller.AddResultHandler(new VoiceCommandResultHandler());
        FocusTracker.Shared.Start();
        // The first take then starts listening at once instead of after half a second of device setup.
        AudioService.Shared.Prepare();
        DictationSounds.Shared.Prepare();
        PasteEngine.Shared.ManualPasteConfirmed += AutoSendSuggestionController.Shared.RecordPaste;

        // The bar is always there: an idle pill until a take starts (Mac: showIdleRecordingWindow).
        var bar = _bar = new FloatingBar(() => controller.Level);
        bar.Moved += (x, y) =>
        {
            var settings = AppSettings.Instance;
            settings.FloatingBarX = x;
            settings.FloatingBarY = y;
            settings.Save();
        };

        var hotkeys = Hotkeys = new GlobalHotkeyService();
        // One take at a time: the shortcut that started it is the one whose release ends it.
        DictationMode? holding = null;
        void Begin(DictationMode mode)
        {
            if (controller.IsCaptureActive) return;
            holding = mode;
            bar.PlaceOnCursorDisplay();
            controller.Start(mode);
        }
        void End(DictationMode mode)
        {
            if (holding != mode) return;
            holding = null;
            _ = controller.StopAsync();
        }

        hotkeys.DictationKeyDown += () => Begin(DictationMode.Dictation);
        hotkeys.DictationKeyUp += () => End(DictationMode.Dictation);
        // Chat completion (Mac: the chat hotkey target, ignored while Chat Mode is off).
        hotkeys.ChatKeyDown += () =>
        {
            if (AppSettings.Instance.ChatModeEnabled) Begin(DictationMode.Chat);
        };
        hotkeys.ChatKeyUp += () => End(DictationMode.Chat);
        hotkeys.CancelPressed += () =>
        {
            holding = null;
            controller.Cancel();
        };

        // A shortcut recorder that is listening must get the combination, not start a take (Mac: hotkeyManagerSuspend).
        void SetHotkeysSuspended(bool suspended)
        {
            if (suspended) hotkeys.Suspend();
            else hotkeys.Resume();
        }
        WeakReferenceMessenger.Default.Register<HotkeyCaptureMessage>(this, (_, message) => SetHotkeysSuspended(message.Active));
        Settings.ShortcutRecorder.RecordingChanged += SetHotkeysSuspended;

        controller.StateChanged += state =>
        {
            hotkeys.CancelArmed = controller.IsCancelable;
            if (state == DictationState.Starting) VoiceCommandService.Shared.RefreshIfNeeded();
            if (state == DictationState.Idle) holding = null;
            bar.SetMode(state switch
            {
                DictationState.Starting => BarMode.Starting,
                DictationState.Recording => BarMode.Recording,
                DictationState.Transcribing => BarMode.Transcribing,
                _ => BarMode.Idle,
            }, chat: controller.Mode == DictationMode.Chat);
        };

        _ = RuntimeConfigService.Shared.FetchAndApplyOnLaunchAsync();
        SessionService.Shared.SessionChanged += change =>
        {
            if (change == SessionChange.SignedIn) _ = RuntimeConfigService.Shared.FetchAndApplyOnLaunchAsync();
        };
    }

    /// Stops the take in flight and releases the hooks and the bar. Safe to call more than once.
    public void Shutdown()
    {
        if (!_started) return;

        DictationController.Shared.Cancel();
        Hotkeys.Dispose();
        _bar?.Dispose();
    }
}
