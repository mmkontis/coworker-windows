using System.Globalization;
using System.Text.RegularExpressions;

namespace Coworker.Paste;

/// <summary>
/// Fits dictated text to the place it is pasted into (Mac: DictationTextRules.continuingSentence and
/// needsLeadingSpace, used by DictationPasteController.fitted). Pure functions with no Windows or WinUI
/// dependency, so the test project compiles this file on its own.
///
/// The rules that need no context (spoken "slash", "dot", "dash", lowercase web addresses) run on the
/// server when a take is sent with text_rules; only the rules that need the text around the cursor live here.
///
/// Difference from the Mac: the Mac also keeps a capital when Apple's name tagger finds an English name
/// ("talked with Minas"). Windows has no such tagger, so names keep their capital only when they are in the
/// dictionary, replace a capitalised selection, or already appear capitalised mid-sentence in the field.
/// </summary>
public static class DictationTextRules
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "etc.", "vs.", "mr.", "mrs.", "ms.", "dr.", "κλπ.", "κτλ.", "κ.",
    };

    /// <summary>
    /// The text as it should be pasted at the cursor: continuing the sentence when the cursor is in the
    /// middle of one and, when <paramref name="spaceAfterSentenceEnd"/> is on, starting with a space right
    /// after ". ! ?". With nothing before the cursor the text is returned unchanged.
    /// </summary>
    public static string Fitted(string text, string before, string selected, string after,
        IReadOnlyCollection<string> dictionary, bool spaceAfterSentenceEnd)
    {
        if (before.Length == 0) return text;
        var continued = ContinuingSentence(text, before, selected, after, dictionary);
        if (!spaceAfterSentenceEnd) return continued;
        return NeedsLeadingSpace(continued, before[^1]) ? " " + continued : continued;
    }

    /// <summary>Dictation that starts right after a sentence end gets a space, so "Done." followed by
    /// "Next step" reads "Done. Next step".</summary>
    public static bool NeedsLeadingSpace(string text, char? previous)
    {
        if (previous is not { } prev || ".!?".IndexOf(prev) < 0 || text.Length == 0) return false;
        var first = text[0];
        return !char.IsWhiteSpace(first) && ".,;:!?)]}".IndexOf(first) < 0;
    }

    /// <summary>
    /// Dictation is transcribed as a sentence of its own, so it starts with a capital. Typed into the middle
    /// of a sentence it continues that sentence, and a final period goes when the sentence carries on after it.
    /// </summary>
    public static string ContinuingSentence(string text, string before, string selected = "", string after = "",
        IReadOnlyCollection<string>? dictionary = null)
    {
        if (!ContinuesSentence(before)) return text;
        var result = text;
        if (FirstWord(text) is { } word && HasSentenceCapital(word) &&
            !KeepsCapital(word, before, selected, after, dictionary ?? Array.Empty<string>()))
        {
            var index = result.IndexOf(word, StringComparison.Ordinal);
            if (index >= 0)
            {
                var firstLength = char.IsHighSurrogate(word[0]) && word.Length > 1 ? 2 : 1;
                var lowered = word.Substring(0, firstLength).ToLowerInvariant() + word.Substring(firstLength);
                result = result.Substring(0, index) + lowered + result.Substring(index + word.Length);
            }
        }
        if (SentenceGoesOn(after)) result = DroppingFinalPeriod(result);
        return result;
    }

    /// <summary>Text before the caret ends mid-sentence: in a word, a number, or after a comma. A sentence end,
    /// a colon, quotes, a new line or an empty field do not.</summary>
    public static bool ContinuesSentence(string before)
    {
        var lineStart = 0;
        for (var i = before.Length - 1; i >= 0; i--)
        {
            if (IsNewline(before[i]))
            {
                lineStart = i + 1;
                break;
            }
        }
        var line = before.Substring(lineStart);

        var lastIndex = line.Length - 1;
        while (lastIndex >= 0 && char.IsWhiteSpace(line[lastIndex])) lastIndex--;
        if (lastIndex < 0) return false;
        var last = line[lastIndex];

        // Comma, middle dot, Greek ano teleia, open bracket, slash, ampersand, plus.
        if (char.IsLetter(last) || char.IsNumber(last) || ",··(/&+".IndexOf(last) >= 0) return true;
        if (char.IsLowSurrogate(last) && lastIndex > 0 && char.IsLetterOrDigit(line, lastIndex - 1)) return true;

        // A hyphen or dash continues a sentence, but one that opens a line is a bullet.
        return "-\u2013\u2014".IndexOf(last) >= 0 && line.Any(char.IsLetter);
    }

    // "I", acronyms and mixed case such as iPhone never count as a sentence capital. A name keeps its
    // capital: a capitalised dictionary word, a word that replaces a capitalised one, or a word the field
    // already capitalises mid-sentence.
    private static bool KeepsCapital(string word, string before, string selected, string after, IReadOnlyCollection<string> dictionary)
    {
        if (word == "I" || word.StartsWith("I'", StringComparison.Ordinal) || word.StartsWith("I’", StringComparison.Ordinal)) return true;
        if (dictionary.Any(entry => FirstWord(entry) == word)) return true;
        foreach (var c in selected)
        {
            if (char.IsLetter(c)) return char.IsUpper(c);
        }
        return IsCapitalisedMidSentence(word, before) || IsCapitalisedMidSentence(word, after);
    }

    private static string? FirstWord(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        if (start == end) return null;

        var token = text.Substring(start, end - start);
        var first = 0;
        while (first < token.Length && !char.IsLetter(token, first)) first++;
        var last = token.Length - 1;
        while (last >= first && !char.IsLetter(token, last) && !(char.IsLowSurrogate(token[last]) && last > 0 && char.IsLetter(token, last - 1))) last--;
        return last < first ? null : token.Substring(first, last - first + 1);
    }

    private static bool HasSentenceCapital(string word)
    {
        if (word.Length == 0 || !char.IsUpper(word, 0)) return false;
        var firstLength = char.IsHighSurrogate(word[0]) && word.Length > 1 ? 2 : 1;
        for (var i = firstLength; i < word.Length; i++)
        {
            if (char.IsUpper(word[i])) return false;
        }
        return true;
    }

    private static bool IsCapitalisedMidSentence(string word, string context)
    {
        if (context.Length == 0) return false;
        var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])";
        foreach (Match match in Regex.Matches(context, pattern, RegexOptions.CultureInvariant))
        {
            if (ContinuesSentence(context.Substring(0, match.Index))) return true;
        }
        return false;
    }

    // The text after the insertion carries on the sentence in lowercase or after a comma.
    private static bool SentenceGoesOn(string after)
    {
        foreach (var c in after)
        {
            if (IsNewline(c)) return false;
            if (char.IsWhiteSpace(c)) continue;
            return char.IsLower(c) || ",:)".IndexOf(c) >= 0;
        }
        return false;
    }

    // "word." loses its period; "...", "e.g.", "etc." and "dikio.gr." keep theirs.
    private static string DroppingFinalPeriod(string text)
    {
        var end = text.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end])) end--;
        if (end < 0 || text[end] != '.') return text;

        var start = end;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var lastWord = text.Substring(start, end - start + 1);
        if (lastWord.Count(c => c == '.') != 1) return text;
        if (Abbreviations.Contains(lastWord.ToLower(CultureInfo.InvariantCulture))) return text;

        return text.Remove(end, 1);
    }

    private static bool IsNewline(char c) =>
        c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';
}
