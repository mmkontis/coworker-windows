using Coworker.Paste;
using Xunit;

namespace Coworker.TextRulesTests;

/// Cases from the Mac's CoworkerTests/DictationTextRulesTests.swift, so both apps fit text the same way.
public class DictationTextRulesTests
{
    private const string GiorgosContext = "Χθες ο Γιώργο δεν ήρθε, αλλά μίλησα με Γιώργο.";
    private static readonly string[] DictionaryWords = { "Hootely", "claude", "AI Greece" };

    [Theory]
    // (dictated, before, selected, after, expected)
    [InlineData("Παρακολουθούσα", "Θα σας ", "Ακολουθούσα", " περισσότερο πέρυσι.", "Παρακολουθούσα")]
    [InlineData("Παρακολουθούσα", "Θα σας ", "ακολουθούσα", " περισσότερο πέρυσι.", "παρακολουθούσα")]
    [InlineData("Παρακολουθούσα", "Θα σας ", "", " περισσότερο πέρυσι.", "παρακολουθούσα")]
    [InlineData("Παρακολουθούσα περισσότερο.", "Θα σας ", "", " πέρυσι.", "παρακολουθούσα περισσότερο")]
    [InlineData("Και την εστίαση.", "τουρισμό, ", "", "", "και την εστίαση.")]
    [InlineData("We should ship it.", "I think ", "", "", "we should ship it.")]
    [InlineData("Followed the plan.", "and we ", "", ", then left.", "followed the plan")]
    [InlineData("Ο Γιώργος είπε", "και ", "", "", "ο Γιώργος είπε")]
    // Sentence starts stay as they are.
    [InlineData("Next step.", "Done. ", "", "", "Next step.")]
    [InlineData("Τι κάνεις;", "Γεια σου; ", "", "", "Τι κάνεις;")]
    [InlineData("Hello there", "", "", "", "Hello there")]
    [InlineData("New paragraph", "First line\n", "", "", "New paragraph")]
    [InlineData("Remember this", "Note: ", "", "", "Remember this")]
    [InlineData("Item one", "- ", "", "", "Item one")]
    // Names keep their capital (the Mac's English name tagger cases are not on Windows).
    [InlineData("I agree", "and ", "", "", "I agree")]
    [InlineData("I'm in", "so ", "", "", "I'm in")]
    [InlineData("OK then", "fine, ", "", "", "OK then")]
    [InlineData("iPhone", "my ", "", "", "iPhone")]
    [InlineData("Γιώργο", "Μίλησα με τον ", "", " χθες", "Γιώργο")]
    [InlineData("Hootely", "we use ", "", " for this", "Hootely")]
    // The period stays when the sentence ends there, or in abbreviations.
    [InlineData("Etc.", "apples, ", "", " and pears", "etc.")]
    [InlineData("Dikio.gr.", "visit ", "", " today", "dikio.gr.")]
    public void ContinuingSentence(string dictated, string before, string selected, string after, string expected)
    {
        var context = dictated == "Γιώργο" ? GiorgosContext + " " + before : before;
        Assert.Equal(expected, DictationTextRules.ContinuingSentence(dictated, context, selected, after, DictionaryWords));
    }

    [Theory]
    [InlineData("Next step", '.', true)]
    [InlineData("/compact", '.', true)]
    [InlineData("Wow", '!', true)]
    [InlineData("Next step", 'a', false)]
    [InlineData("Next step", ' ', false)]
    [InlineData(" Next step", '.', false)]
    [InlineData(", and more", '.', false)]
    [InlineData("", '.', false)]
    public void LeadingSpace(string text, char previous, bool expected) =>
        Assert.Equal(expected, DictationTextRules.NeedsLeadingSpace(text, previous));

    [Fact]
    public void NoLeadingSpaceWithoutAPreviousCharacter() =>
        Assert.False(DictationTextRules.NeedsLeadingSpace("Next step", null));

    [Theory]
    // (dictated, before, after, space after a sentence end, expected): what the paste engine types.
    [InlineData("Next step", "Done.", "", true, " Next step")]
    [InlineData("Next step", "Done.", "", false, "Next step")]
    [InlineData("We should ship it.", "I think ", "", true, "we should ship it.")]
    [InlineData("Hello there", "", "", true, "Hello there")]
    [InlineData("Followed the plan.", "and we ", ", then left.", true, "followed the plan")]
    public void Fitted(string dictated, string before, string after, bool spaceAfterSentenceEnd, string expected) =>
        Assert.Equal(expected, DictationTextRules.Fitted(dictated, before, "", after, DictionaryWords, spaceAfterSentenceEnd));

    [Theory]
    [InlineData("I think", true)]
    [InlineData("so, ", true)]
    [InlineData("version 2", true)]
    [InlineData("a - ", true)]
    [InlineData("- ", false)]
    [InlineData("Done. ", false)]
    [InlineData("Note:", false)]
    [InlineData("First line\n", false)]
    [InlineData("", false)]
    public void ContinuesSentence(string before, bool expected) =>
        Assert.Equal(expected, DictationTextRules.ContinuesSentence(before));
}
