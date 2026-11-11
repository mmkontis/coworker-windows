using Coworker.Controls;
using Coworker.Models;

namespace Coworker.Services;

/// The choices offered by the tray panel and menu (Mac: LanguageConstants and TranscriptionModelCatalog).
public static class TrayOptions
{
    /// Mac displayLanguages, without the flag emoji (Windows draws them as letters).
    public static readonly (string Code, string Name)[] DisplayLanguages =
    {
        ("auto", "Auto Detect"),
        ("en", "English"),
        ("el", "Greek (Ελληνικά)"),
        ("es", "Spanish (Español)"),
        ("fr", "French (Français)"),
        ("de", "German (Deutsch)"),
        ("it", "Italian (Italiano)"),
        ("pt", "Portuguese (Português)"),
        ("ru", "Russian (Русский)"),
        ("ja", "Japanese (日本語)"),
        ("ko", "Korean (한국어)"),
        ("zh", "Chinese (中文)"),
        ("ar", "Arabic (العربية)"),
        ("hi", "Hindi (हिन्दी)"),
    };

    /// Mac translationLanguages.
    public static readonly (string Code, string Name)[] TranslationLanguages =
    {
        ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("it", "Italian"),
        ("pt", "Portuguese"), ("ru", "Russian"), ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese"),
        ("ar", "Arabic"), ("hi", "Hindi"), ("nl", "Dutch"), ("sv", "Swedish"), ("da", "Danish"),
        ("no", "Norwegian"), ("fi", "Finnish"), ("pl", "Polish"), ("tr", "Turkish"), ("cs", "Czech"),
        ("hu", "Hungarian"), ("ro", "Romanian"), ("bg", "Bulgarian"), ("hr", "Croatian"), ("sk", "Slovak"),
        ("sl", "Slovenian"), ("et", "Estonian"), ("lv", "Latvian"), ("lt", "Lithuanian"), ("uk", "Ukrainian"),
        ("el", "Greek"), ("he", "Hebrew"), ("th", "Thai"), ("vi", "Vietnamese"), ("id", "Indonesian"),
        ("ms", "Malay"), ("tl", "Filipino"), ("fa", "Persian"), ("bn", "Bengali"), ("ur", "Urdu"),
        ("ta", "Tamil"), ("te", "Telugu"), ("ml", "Malayalam"), ("kn", "Kannada"), ("gu", "Gujarati"),
        ("pa", "Punjabi"), ("mr", "Marathi"), ("ne", "Nepali"), ("si", "Sinhala"), ("my", "Myanmar"),
        ("km", "Khmer"), ("lo", "Lao"), ("ka", "Georgian"), ("am", "Amharic"), ("sw", "Swahili"),
        ("yo", "Yoruba"), ("zu", "Zulu"), ("af", "Afrikaans"), ("sq", "Albanian"), ("az", "Azerbaijani"),
        ("be", "Belarusian"), ("bs", "Bosnian"), ("eu", "Basque"), ("gl", "Galician"), ("is", "Icelandic"),
        ("ga", "Irish"), ("mk", "Macedonian"), ("mt", "Maltese"), ("cy", "Welsh"), ("hy", "Armenian"),
    };

    /// The panel's three model stops, slowest to smartest (Mac ModelTierPicker).
    public static readonly (string Code, string Name)[] ModelTiers =
    {
        ("nari-qwen3-asr-fast", "Qwen Fast"),
        ("gpt-transcribe", "GPT transcription"),
        ("microsoft/mai-transcribe-2", "Microsoft MAI-Transcribe 2"),
    };

    /// The stops of the menu's model row, from the server catalog (Mac TranscriptionModelCatalog.stops).
    public static (string Name, string Code)[] ModelStops =>
        TranscriptionModelCatalog.Stops.Select(s => (s.Label, s.Code)).ToArray();

    /// Models offered by the menu: the server catalog when it has loaded, else the four stops.
    public static IReadOnlyList<(string Code, string Name)> ModelChoices(IReadOnlyList<TranscriptionModel>? server)
    {
        if (server is { Count: > 0 }) return server.Select(m => (m.Code, m.Name)).ToList();
        return ModelStops.Select(s => (s.Code, s.Name)).ToList();
    }

    public static string LanguageName(string code) =>
        DisplayLanguages.FirstOrDefault(l => l.Code == code).Name ?? code.ToUpperInvariant();

    public static string TranslationName(string code) =>
        TranslationLanguages.FirstOrDefault(l => l.Code == code).Name ?? code;

    /// Mac TranscriptionModelCatalog.persist: choosing a model marks it as the user's own choice, so
    /// the remote runtime config no longer replaces it.
    public static void SelectModel(string code)
    {
        var settings = AppSettings.Instance;
        settings.SelectedModel = code;
        settings.UserOverrodeSelectedModel = true;
        settings.Save();
    }
}
