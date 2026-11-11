using Coworker.Models;

namespace Coworker.Services.VoiceCommands;

/// <param name="Arguments">What is left of the utterance once the trigger phrase is taken out.</param>
public sealed record VoiceCommandMatch(VoiceCommand Command, string Arguments);

/// The Mac's matchCommand (VoiceCommandManager.swift), including its quirks: no word boundaries
/// ("callback" matches "call") and punctuation stays in the arguments.
public static class VoiceCommandMatcher
{
    /// Case-insensitive match of the utterance against every trigger phrase of the given commands.
    /// Longer phrases win over shorter ones across all commands, so a multi-word trigger beats its
    /// own first word.
    public static VoiceCommandMatch? Match(IEnumerable<VoiceCommand> commands, string text)
    {
        var utterance = text.Trim();
        if (utterance.Length == 0) return null;

        var candidates = commands
            .SelectMany(command => command.AllTriggerPhrases.Select(phrase => (command, phrase)))
            .OrderByDescending(candidate => candidate.phrase.Length);

        foreach (var (command, phrase) in candidates)
        {
            switch (command.MatchMode)
            {
                case "exact":
                    if (utterance.Equals(phrase, StringComparison.OrdinalIgnoreCase))
                        return new VoiceCommandMatch(command, "");
                    break;

                case "starts_with":
                    if (utterance.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
                        return new VoiceCommandMatch(command, utterance[phrase.Length..].Trim());
                    break;

                case "contains":
                    var index = utterance.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                        return new VoiceCommandMatch(command, (utterance[..index] + " " + utterance[(index + phrase.Length)..]).Trim());
                    break;
            }
        }

        return null;
    }
}
