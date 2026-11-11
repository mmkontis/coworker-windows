using System.Collections.Specialized;
using System.Text.Json;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Transcription;

/// <summary>
/// The dictionary words sent with every take (Mac: the coworker_dictionary_json cache). The list is
/// kept in step with <see cref="AppState"/> and saved to disk, so the first dictation after launch does
/// not have to wait for the server; sign-out clears it. UI thread only.
/// </summary>
public static class DictionaryCache
{
    private static readonly string CachePath = AppPaths.GetPath("dictionary.json");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// Extra words the transcriber should know, such as voice-command trigger phrases (Mac:
    /// VoiceCommandManager.triggerPhrasesAsDictionaryEntries). A feature registers its provider once.
    public static readonly List<Func<IEnumerable<string>>> ExtraWordProviders = new();

    private static List<DictionaryEntryPayload> _entries = new();
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private static bool _started;

    /// Loads the saved words and starts following the app state. Call once at startup.
    public static void Start()
    {
        if (_started) return;
        _started = true;

        _entries = Load();
        AppState.Shared.DictionaryEntries.CollectionChanged += OnEntriesChanged;
        SessionService.Shared.SessionChanged += change =>
        {
            if (change is SessionChange.SignedOut or SessionChange.Invalidated) Clear();
        };
    }

    /// The words to send: the dictionary, then the extra words (each once, ignoring case).
    public static IReadOnlyList<DictionaryEntryPayload> Snapshot()
    {
        var combined = new List<DictionaryEntryPayload>(_entries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in ExtraWordProviders)
        {
            foreach (var word in provider())
            {
                if (!string.IsNullOrEmpty(word) && seen.Add(word)) combined.Add(new DictionaryEntryPayload(null, word));
            }
        }
        return combined;
    }

    /// The user's own dictionary words, without the extra words (Mac: dictionaryWords()). The paste engine
    /// uses them to keep a name's capital mid-sentence.
    public static IReadOnlyList<string> Words() => _entries.Select(entry => entry.Word).ToList();

    private static void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A reload clears and refills the collection; save once it settles.
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSaveTimer()
    {
        var timer = UiThread.Queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _entries = AppState.Shared.DictionaryEntries.Select(ToPayload).ToList();
            Save();
        };
        return timer;
    }

    // The definition only travels when it says something the word does not (Mac: cacheDictionaryForTranscription).
    private static DictionaryEntryPayload ToPayload(DictionaryEntry entry) => new(
        entry.Id,
        entry.Word,
        entry.Definition.Length > 0 && entry.Definition != entry.Word ? entry.Definition : null,
        string.IsNullOrEmpty(entry.Shortcut) ? null : entry.Shortcut);

    private static void Clear()
    {
        _saveTimer?.Stop();
        _entries = new List<DictionaryEntryPayload>();
        try { File.Delete(CachePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static List<DictionaryEntryPayload> Load()
    {
        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<List<DictionaryEntryPayload>>(File.ReadAllText(CachePath), Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write($"Dictionary cache not read: {ex.Message}");
            return new();
        }
    }

    private static void Save()
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(_entries)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"Dictionary cache not saved: {ex.Message}");
        }
    }
}
