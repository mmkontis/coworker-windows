using System.Globalization;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Views;

namespace Coworker.Services.VoiceCommands;

/// <param name="ForegroundWindow">The window the user was dictating into: the feedback windows go on its
/// monitor and it gets the focus back after the inline input.</param>
public sealed record VoiceCommandContext(IntPtr ForegroundWindow);

/// <summary>
/// Custom voice commands ("Shortcuts"): the cached list of enabled commands, the matcher that
/// decides whether a dictation is a command, and the execution with its feedback (badge, toast).
/// Mac: VoiceCommandManager plus the command branch of onTranscriptionComplete in CoworkerApp.swift.
/// Everything is inert while nobody is signed in.
/// </summary>
public sealed class VoiceCommandService
{
    public static VoiceCommandService Shared { get; } = new();

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstFetchWait = TimeSpan.FromSeconds(2);
    private const int ToastPreviewLength = 80;
    private const double ToastSeconds = 4;

    private readonly VoiceCommandExecutor _executor = new();
    private readonly object _gate = new();
    private IReadOnlyList<VoiceCommand> _commands = Array.Empty<VoiceCommand>();
    private DateTime? _lastFetch;
    private Task? _fetch;

    private VoiceCommandService()
    {
        SessionService.Shared.SessionChanged += OnSessionChanged;
    }

    /// The enabled commands as of the last fetch.
    public IReadOnlyList<VoiceCommand> Commands => _commands;

    /// The catalog behind the step picker, with the two local actions always present.
    public IReadOnlyList<ActionCatalogEntry> ActionsCatalog { get; private set; } = LocalActions();

    /// Fetches the commands unless the cache is fresh and non-empty (so a user with no enabled command
    /// is asked again every time). Call at launch and when a recording starts, like the Mac does. Fire and forget.
    public void RefreshIfNeeded() => _ = StartRefreshIfNeeded();

    private Task? StartRefreshIfNeeded()
    {
        if (_lastFetch is { } last && DateTime.UtcNow - last < CacheLifetime && _commands.Count > 0) return null;
        return FetchAsync();
    }

    /// Loads every command of the user (disabled ones too, for the Shortcuts page) and refreshes the
    /// cache of enabled ones from the same response.
    public async Task<List<VoiceCommand>> LoadAllAsync()
    {
        var all = await ApiService.Shared.GetVoiceCommands();
        Store(all);
        return all;
    }

    public async Task CreateAsync(VoiceCommandInput input)
    {
        await ApiService.Shared.CreateVoiceCommand(input);
        InvalidateCache();
    }

    public async Task UpdateAsync(string id, VoiceCommandInput input)
    {
        await ApiService.Shared.UpdateVoiceCommand(id, input);
        InvalidateCache();
    }

    public async Task DeleteAsync(string id)
    {
        await ApiService.Shared.DeleteVoiceCommand(id);
        InvalidateCache();
    }

    public void InvalidateCache() => _lastFetch = null;

    /// Loads the action catalog and makes sure the two actions that run on this machine are in it. When
    /// the server cannot be reached the picker still offers those two.
    public async Task<IReadOnlyList<ActionCatalogEntry>> LoadActionsCatalogAsync()
    {
        try
        {
            var catalog = await ApiService.Shared.GetActionsCatalog();
            var local = LocalActions();   // open_web_page, open_app
            for (var i = local.Count - 1; i >= 0; i--)
            {
                if (catalog.All(a => a.Id != local[i].Id)) catalog.Insert(0, local[i]);
            }
            ActionsCatalog = catalog;
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Voice commands: action catalog unavailable ({ex.Kind})");
        }
        return ActionsCatalog;
    }

    /// The trigger phrases (each alternative on its own, no duplicates) to prime transcription with,
    /// so the model spells words like "hubspot" the way the user configured them.
    public IReadOnlyList<string> TriggerPhrasesForPriming() => _commands
        .SelectMany(command => command.AllTriggerPhrases)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// Runs the dictation as a command when it matches one. Returns true when a command took it, in
    /// which case the text must not be pasted. The command itself runs in the background; its badge
    /// and result toast appear on their own. Call on the UI thread.
    public async Task<bool> TryHandleAsync(string transcript, VoiceCommandContext context)
    {
        if (!SessionService.Shared.IsAuthenticated)
            return false;

        // A dictation right after launch has no list yet: give the first fetch a moment instead of pasting a command.
        var refresh = StartRefreshIfNeeded();
        if (_lastFetch == null && refresh != null) await Task.WhenAny(refresh, Task.Delay(FirstFetchWait));

        var match = VoiceCommandMatcher.Match(_commands, transcript);
        if (match == null) return false;

        var command = match.Command;
        if (command.ActionType == "open_web_page" && match.Arguments.Length == 0 && command.UsesContext)
        {
            // "search" with nothing after it: ask for the text the URL needs.
            var input = new CommandInputWindow(command, context.ForegroundWindow);
            input.Submitted += text => _ = RunAsync(command, transcript, text, context);
            input.ShowAndFocus();
            return true;
        }

        _ = RunAsync(command, transcript, match.Arguments, context);
        return true;
    }

    private async Task RunAsync(VoiceCommand command, string transcript, string arguments, VoiceCommandContext context)
    {
        UiThread.Post(() => CommandExecutedWindow.ShowFor(command, context.ForegroundWindow));

        var result = await _executor.ExecuteAsync(command, transcript, arguments);
        ToastService.Show(result.Success ? ToastKind.Success : ToastKind.Error, null,
            $"{TitleCase(command.TriggerPhrase)}: {Truncate(result.Result, ToastPreviewLength)}", ToastSeconds);
    }

    // Coalesces concurrent fetches; errors keep the cache that is there.
    private Task FetchAsync()
    {
        lock (_gate) return _fetch ??= Task.Run(FetchCoreAsync);
    }

    private async Task FetchCoreAsync()
    {
        try
        {
            if (SessionService.Shared.IsAuthenticated) Store(await ApiService.Shared.GetVoiceCommands());
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Voice commands: fetch failed ({ex.Kind} {ex.StatusCode})");
        }
        finally
        {
            lock (_gate) _fetch = null;
        }
    }

    private void Store(IEnumerable<VoiceCommand> all)
    {
        _commands = all.Where(command => command.IsEnabled).ToList();
        _lastFetch = DateTime.UtcNow;
    }

    private void OnSessionChanged(SessionChange change)
    {
        switch (change)
        {
            case SessionChange.SignedIn:
                InvalidateCache();
                RefreshIfNeeded();
                break;
            case SessionChange.SignedOut or SessionChange.Invalidated:
                _commands = Array.Empty<VoiceCommand>();
                InvalidateCache();
                break;
        }
    }

    private static List<ActionCatalogEntry> LocalActions() => new()
    {
        new() { Id = "open_web_page", Name = "Open Web Page", Description = "Open a web page in the default browser", IconName = "globe", IconColor = "#007AFF" },
        new() { Id = "open_app", Name = "Open App", Description = "Launch a specific Windows application", IconName = "app.badge", IconColor = "#FF9500" },
    };

    // Swift's capitalized: the first letter of every word up, the rest down ("call|phone" -> "Call|Phone").
    private static string TitleCase(string text) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture));

    private static string Truncate(string text, int length)
    {
        if (text.Length <= length) return text;
        // Do not cut a surrogate pair in half.
        return char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];
    }
}
