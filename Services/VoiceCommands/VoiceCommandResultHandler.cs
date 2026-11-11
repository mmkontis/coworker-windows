using Coworker.Dictation;

namespace Coworker.Services.VoiceCommands;

/// A dictation that matches a custom voice command runs the command instead of being pasted.
public sealed class VoiceCommandResultHandler : IDictationResultHandler
{
    public Task<bool> TryHandleAsync(string transcript) =>
        VoiceCommandService.Shared.TryHandleAsync(transcript, new VoiceCommandContext(Coworker.Paste.ForegroundApp.Current()?.Window ?? IntPtr.Zero));
}
