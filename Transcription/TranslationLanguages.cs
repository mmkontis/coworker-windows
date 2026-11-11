namespace Coworker.Transcription;

/// The languages dictation can be translated into (Mac: LanguageConstants.translationLanguages).
public static class TranslationLanguages
{
    public static readonly (string Code, string Name)[] All =
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

    /// The English name of a language code, or the code itself when it is not in the list.
    public static string NameOf(string code) =>
        All.FirstOrDefault(l => l.Code == code).Name ?? code;
}
