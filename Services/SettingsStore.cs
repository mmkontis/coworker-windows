using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coworker.Helpers;
using Coworker.Models;

namespace Coworker.Services;

/// <summary>
/// Persistence for <see cref="AppSettings"/>: schema-versioned, written atomically (temp file +
/// replace, keeping the previous file as .bak), debounced off the calling thread, and never
/// silently replacing a file it failed to parse.
/// </summary>
public static class SettingsStore
{
    public const int CurrentSchemaVersion = 2;

    private const int DebounceMs = 250;

    private static readonly string SettingsPath = AppPaths.GetPath("settings.json");
    private static readonly string BackupPath = SettingsPath + ".bak";

    // Reads accept the old PascalCase files; writes are camelCase from here on.
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep "Ctrl+Shift" readable
    };

    private static readonly object Gate = new();
    private static readonly Timer FlushTimer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    private static string? _pendingJson;

    static SettingsStore()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    internal static AppSettings Load()
    {
        AppSettings? settings = null;
        var needsSave = false;
        var movedToken = false;

        if (File.Exists(SettingsPath))
        {
            settings = TryRead(SettingsPath, out needsSave, out movedToken);
            if (settings == null)
            {
                QuarantineCorruptFile();
                settings = TryRead(BackupPath, out _, out movedToken);
                needsSave = true;
            }
        }

        settings ??= new AppSettings();
        if (needsSave) QueueSave(settings);
        if (movedToken)
        {
            // Rewrite now and drop the .bak the rewrite creates: it is the file that held the plaintext token.
            Flush();
            File.Delete(BackupPath);
        }
        return settings;
    }

    /// Serializes now (cheap, and safe against later mutation) and writes after a short debounce.
    internal static void QueueSave(AppSettings settings)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, WriteOptions);
        }
        catch (InvalidOperationException)
        {
            // A list was mutated on another thread mid-serialize; the next Save() will pick it up.
            return;
        }

        lock (Gate) _pendingJson = json;
        FlushTimer.Change(DebounceMs, Timeout.Infinite);
    }

    /// Writes any pending save immediately. Called on exit so no change is lost.
    public static void Flush()
    {
        lock (Gate)
        {
            if (_pendingJson == null) return;
            try
            {
                WriteAtomic(_pendingJson);
                _pendingJson = null;
            }
            catch (Exception ex)
            {
                // Keep the pending JSON: the next Save() or Flush() retries.
                DebugLog.Write($"SettingsStore: write failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
    }

    private static void WriteAtomic(string json)
    {
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, BackupPath, ignoreMetadataErrors: true);
        else File.Move(tmp, SettingsPath);
    }

    private static AppSettings? TryRead(string path, out bool needsSave, out bool movedToken)
    {
        needsSave = false;
        movedToken = false;
        try
        {
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                throw new JsonException("Settings root is not a JSON object.");

            var fileVersion = TakeValue(root, "schemaVersion")?.GetValue<int>() ?? 0;
            var legacyToken = TakeValue(root, "accessToken")?.GetValue<string>();

            var settings = root.Deserialize<AppSettings>(ReadOptions) ?? new AppSettings();

            if (!string.IsNullOrWhiteSpace(legacyToken))
            {
                // One-time move of the plaintext token out of settings.json.
                CredentialStore.Shared.Set(CredentialStore.SessionAccessToken, legacyToken);
                needsSave = true;
                movedToken = true;
            }
            else if (legacyToken != null)
            {
                needsSave = true;
            }

            if (fileVersion < CurrentSchemaVersion)
            {
                Migrate(settings, fileVersion);
                needsSave = true;
            }

            if (!HotkeyShortcut.TryParse(settings.ChatShortcut, out var chat) || !chat.IsValid)
            {
                settings.ChatShortcut = AppSettings.DefaultChatShortcut;
                needsSave = true;
            }

            // A dictation shortcut that can never start a take (Ctrl+A and the like, saved by an older
            // recorder) would show in the app while the default quietly does the work: show the truth.
            if (!HotkeyShortcut.TryParse(settings.DictationShortcut, out var dictation) || !dictation.IsValid)
            {
                DebugLog.Write($"SettingsStore: dictation shortcut '{settings.DictationShortcut}' can't be used, reset to the default");
                settings.DictationShortcut = AppSettings.DefaultDictationShortcut;
                needsSave = true;
            }
            settings.SchemaVersion = Math.Max(fileVersion, CurrentSchemaVersion);
            return settings;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"SettingsStore: could not read {Path.GetFileName(path)} ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private static void Migrate(AppSettings settings, int fromVersion)
    {
        if (fromVersion < 2)
        {
            // Chat's old default never did anything on Windows (there was no chat-completion voice mode)
            // and trips over the Ctrl+Shift dictation hold.
            if (string.Equals(settings.ChatShortcut, "Ctrl+Shift+C", StringComparison.OrdinalIgnoreCase))
                settings.ChatShortcut = AppSettings.DefaultChatShortcut;
        }

        if (fromVersion < 1)
        {
            // An install that predates the first-run onboarding has been set up already.
            settings.HasCompletedOnboarding = true;

            // A bare "Ctrl" dictation trigger could never register (RegisterHotKey needs a real key)
            // and fired on every Ctrl+C/Ctrl+V hold. Upgrade it, and any empty value, to Ctrl+Shift.
            var shortcut = settings.DictationShortcut?.Trim();
            if (string.IsNullOrEmpty(shortcut) ||
                shortcut.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                shortcut.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                settings.DictationShortcut = AppSettings.DefaultDictationShortcut;
            }
        }
    }

    /// Removes and returns a property matched case-insensitively.
    private static JsonValue? TakeValue(JsonObject obj, string name)
    {
        var key = obj.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key == null) return null;
        var value = obj[key] as JsonValue;
        obj.Remove(key);
        return value;
    }

    private static void QuarantineCorruptFile()
    {
        try
        {
            var target = $"{SettingsPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(SettingsPath, target, overwrite: true);
            DebugLog.Write($"SettingsStore: unreadable settings.json moved to {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"SettingsStore: could not quarantine settings.json ({ex.Message})");
        }
    }
}
