namespace Coworker.Models;

// Dictation: language, model, translation, shortcuts, floating bar, audio, paste.
public partial class AppSettings
{
    public const string DefaultDictationShortcut = "Ctrl+Shift";
    public const string DefaultTranscriptionModel = "microsoft/mai-transcribe-2";

    public string SelectedModel { get; set; } = DefaultTranscriptionModel;
    public bool UserOverrodeSelectedModel { get; set; }
    public string SelectedLanguage { get; set; } = "auto";
    public bool TranslationEnabled { get; set; }
    public string SelectedTranslationLanguage { get; set; } = "en";
    public string CustomPrompt { get; set; } = "";
    public bool PrivacyModeEnabled { get; set; } = true;
    public bool DictationSoundsEnabled { get; set; } = true;

    /// Mac dictationPasteToLastInput: paste into the last focused text field instead of the active app.
    public bool DictationPasteToLastInput { get; set; } = true;

    /// Mac dictationSpaceAfterSentenceEnd: dictation pasted right after ". ! ?" starts with a space.
    public bool SpaceAfterSentenceEnd { get; set; } = true;

    /// Mac stackOverlappingRecordings: a new take may start while the previous one is still transcribing.
    public bool StackOverlappingRecordings { get; set; } = true;

    // Hold-to-dictate. "Ctrl+Shift" is a modifier-only hold, "Ctrl+Shift+D" a key chord. Both are
    // detected by the low-level keyboard hook in GlobalHotkeyService.
    public string DictationShortcut { get; set; } = DefaultDictationShortcut;

    /// Where the user last dragged the floating bar (physical pixels, top-left of its window). Null = default spot.
    public int? FloatingBarX { get; set; }
    public int? FloatingBarY { get; set; }

    public string AudioQualityPreset { get; set; } = "compact_16k";
    public bool UserOverrodeAudioQualityPreset { get; set; }

    /// Remote runtime-config bookkeeping (GET /coworker/runtime-config).
    public double? RuntimeConfigLastFetchedAt { get; set; }
    public int? RuntimeConfigLastVersion { get; set; }

    // Local ASR engine: "server" = cloud transcription via the API.
    public string LocalAsrEngine { get; set; } = "server";

    public bool OpenRouterDirectTranscriptionEnabled { get; set; }
    public List<OpenRouterModel> CustomOpenRouterTranscriptionModels { get; set; } = new();

    public record OpenRouterModel(string Id, string Name);
}
