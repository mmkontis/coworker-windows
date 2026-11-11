using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Settings;

/// <summary>
/// The Settings page's view of <see cref="AppSettings"/>: every property reads and writes the real
/// setting and saves it, and the page follows changes made elsewhere (tray, recording bar, theme
/// popover) because AppSettings raises Changed after each save.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Instance;

    /// Start following AppSettings.Changed (page loaded).
    public void Attach() => AppSettings.Changed += OnSettingsChanged;

    /// Stop following (page unloaded).
    public void Detach() => AppSettings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged() => UiThread.Post(() => OnPropertyChanged(string.Empty));

    private void Update<T>(T current, T value, Action assign, Action? afterSave = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign();
        _settings.Save();
        afterSave?.Invoke();
        OnPropertyChanged(name);
    }

    // General
    public bool PasteToLastInput
    {
        get => _settings.DictationPasteToLastInput;
        set => Update(_settings.DictationPasteToLastInput, value, () => _settings.DictationPasteToLastInput = value);
    }

    public bool SpaceAfterSentenceEnd
    {
        get => _settings.SpaceAfterSentenceEnd;
        set => Update(_settings.SpaceAfterSentenceEnd, value, () => _settings.SpaceAfterSentenceEnd = value);
    }

    public bool TranslationEnabled
    {
        get => _settings.TranslationEnabled;
        set => Update(_settings.TranslationEnabled, value, () => _settings.TranslationEnabled = value);
    }

    public string SelectedTranslationLanguage
    {
        get => _settings.SelectedTranslationLanguage;
        set => Update(_settings.SelectedTranslationLanguage, value, () => _settings.SelectedTranslationLanguage = value);
    }

    public bool PrivacyModeEnabled
    {
        get => _settings.PrivacyModeEnabled;
        set => Update(_settings.PrivacyModeEnabled, value, () => _settings.PrivacyModeEnabled = value);
    }

    public bool DictationSoundsEnabled
    {
        get => _settings.DictationSoundsEnabled;
        set => Update(_settings.DictationSoundsEnabled, value, () => _settings.DictationSoundsEnabled = value);
    }

    public bool AutoStartEnabled
    {
        get => _settings.AutoStartEnabled;
        set => Update(_settings.AutoStartEnabled, value, () => _settings.AutoStartEnabled = value);
    }

    // Voice Shortcuts and the clipboard shortcut. The hotkey service re-reads the settings when asked.
    public string DictationShortcut
    {
        get => _settings.DictationShortcut;
        set => Update(_settings.DictationShortcut, value, () => _settings.DictationShortcut = value, ApplyShortcuts);
    }

    public string ClipboardShortcut
    {
        get => _settings.ClipboardShortcut;
        set => Update(_settings.ClipboardShortcut, value, () => _settings.ClipboardShortcut = value, ApplyShortcuts);
    }

    public void ResetDictationShortcut() => DictationShortcut = AppSettings.DefaultDictationShortcut;

    private static void ApplyShortcuts() => GlobalHotkeyService.Instance?.RegisterAllHotkeys();

    // Clipboard History
    public bool ClipboardHistoryEnabled
    {
        get => _settings.ClipboardHistoryEnabled;
        set => Update(_settings.ClipboardHistoryEnabled, value, () => _settings.ClipboardHistoryEnabled = value);
    }

    public bool AutoPaste
    {
        get => _settings.AutoPaste;
        set => Update(_settings.AutoPaste, value, () => _settings.AutoPaste = value);
    }

    public bool AutoSubmitEnabled
    {
        get => _settings.AutoSubmitEnabled;
        set => Update(_settings.AutoSubmitEnabled, value, () => _settings.AutoSubmitEnabled = value);
    }

    public bool ShowMediaBar
    {
        get => _settings.ShowMediaBar;
        set => Update(_settings.ShowMediaBar, value, () => _settings.ShowMediaBar = value);
    }

    public bool ClearHistoryOnQuit
    {
        get => _settings.ClearHistoryOnQuit;
        set => Update(_settings.ClearHistoryOnQuit, value, () => _settings.ClearHistoryOnQuit = value);
    }

    /// Kept at 10 or more, as the Mac does.
    public int MaxHistoryItems
    {
        get => _settings.MaxHistoryItems;
        set => Update(_settings.MaxHistoryItems, Math.Max(value, 10), () => _settings.MaxHistoryItems = Math.Max(value, 10));
    }

    public IReadOnlyList<string> AutoSubmitSites => _settings.AutoSubmitSites;

    public void RemoveAutoSubmitSite(string site)
    {
        if (_settings.AutoSubmitSites.RemoveAll(s => s.Equals(site, StringComparison.OrdinalIgnoreCase)) == 0) return;
        _settings.Save();
    }

    public void SetAutoSubmitApp(string appName, bool enabled)
    {
        AutoEnterApps.Set(enabled, appName);
        _settings.Save();
    }

    // Model & Language
    public string SelectedLanguage
    {
        get => _settings.SelectedLanguage;
        set => Update(_settings.SelectedLanguage, value, () => _settings.SelectedLanguage = value);
    }

    // Audio Quality
    public string AudioQualityPreset => _settings.AudioQualityPreset;

    public void SelectAudioQualityPreset(string preset)
    {
        _settings.AudioQualityPreset = preset;
        _settings.UserOverrodeAudioQualityPreset = true;
        _settings.Save();
    }

    // Diagnostics
    public bool VerboseLogging
    {
        get => _settings.VerboseLogging;
        set => Update(_settings.VerboseLogging, value, () => _settings.VerboseLogging = value);
    }
}
