using System.Text;

namespace Coworker.Paste;

/// How a paste is proven: what the field held before, and whether it holds the dictated words now
/// (Mac: DictationPasteController.Receipt). Rich editors report their contents their own way
/// (paragraphs, placeholders, non-breaking spaces, smart quotes), so besides an exact prediction the
/// words themselves, whitespace collapsed, must occur one more time than before.
internal sealed class PasteReceipt
{
    private readonly string _before;
    private readonly string? _expected;
    private readonly int? _requiredCaret;
    private readonly string _words;
    private readonly int _occurrencesBefore;

    /// <param name="before">The field's text before the paste.</param>
    /// <param name="inserted">The dictated text.</param>
    /// <param name="expected">The exact text the field should hold afterwards, when the caret was readable.</param>
    /// <param name="requiredCaret">The caret position that proves a paste when the text itself does not change.</param>
    public PasteReceipt(string before, string inserted, string? expected, int? requiredCaret = null)
    {
        _before = before;
        _expected = expected;
        _requiredCaret = requiredCaret;
        _words = Collapsed(inserted);
        _occurrencesBefore = Occurrences(_words, Collapsed(before));
    }

    /// The caret position, not the text, is the proof (the text is the same before and after).
    public bool CaretProves => _requiredCaret != null;

    /// True when <paramref name="actual"/> proves the words went in. <paramref name="caret"/> is the field's
    /// caret now, when readable.
    public bool Proves(string actual, int? caret)
    {
        if (_expected != null)
        {
            var caretConfirmed = _requiredCaret == null || caret == _requiredCaret;
            // Rich web editors keep visible trailing spaces as non-breaking spaces.
            if (caretConfirmed && (actual == _expected || Normalized(actual) == Normalized(_expected))) return true;
        }
        return _words.Length > 0 && Occurrences(_words, Collapsed(actual)) > _occurrencesBefore;
    }

    /// The field holds what it held before, so a paste that should have changed the text did nothing.
    public bool IsUnchanged(string actual) => _requiredCaret == null && Normalized(actual) == Normalized(_before);

    public static string Normalized(string value) =>
        value.Replace(' ', ' ').Replace("\r\n", "\n").Normalize(NormalizationForm.FormC);

    public static string Collapsed(string value)
    {
        var text = Normalized(value)
            .Replace('’', '\'').Replace('‘', '\'')
            .Replace('“', '"').Replace('”', '"');
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// Non-overlapping occurrences of <paramref name="needle"/>; zero for an empty needle.
    public static int Occurrences(string needle, string haystack)
    {
        if (needle.Length == 0) return 0;

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// What the field should hold after inserting <paramref name="text"/> at a selection of the given size.
    /// The receipt of an empty field, or of a selection that could not be read, has no exact prediction.
    public static PasteReceipt? Predict(string? before, int? selectionStart, int selectionLength, string text)
    {
        if (before == null) return null;
        if (before.Length == 0) return new PasteReceipt(before, text, text);
        if (selectionStart is not { } start || start < 0 || selectionLength < 0 || start > before.Length || selectionLength > before.Length - start)
            return new PasteReceipt(before, text, null);

        var after = before[..start] + text + before[(start + selectionLength)..];
        return new PasteReceipt(before, text, after, before == after ? start + text.Length : null);
    }
}
