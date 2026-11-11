using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coworker.Models;

/// <summary>
/// One step of a voice command. Config values are kept as raw JSON: the server stores any JSON in
/// action_config (the web dashboard writes arrays and numbers), and the editor only ever writes the
/// string keys it owns, so everything else round-trips untouched.
/// </summary>
public sealed class VoiceCommandAction
{
    private Dictionary<string, JsonElement> _actionConfig = new();

    /// Uppercase GUID, like the Mac's UUID().uuidString.
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();

    [JsonPropertyName("action_type")]
    public string ActionType { get; set; } = "";

    [JsonPropertyName("action_config")]
    public Dictionary<string, JsonElement> ActionConfig
    {
        get => _actionConfig;
        set => _actionConfig = value ?? new();
    }

    public string? GetConfig(string key) => ActionConfig.TryGetValue(key, out var value) ? ConfigText(value) : null;

    public void SetConfig(string key, string value) => ActionConfig[key] = JsonSerializer.SerializeToElement(value);

    /// A string as-is, anything else as its raw JSON.
    internal static string ConfigText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };
}

/// A user-defined trigger phrase and the action steps it runs (Mac: VoiceCommand in DataModels.swift).
/// Multi-step commands live in action_config["_actions"] as a JSON string; the top-level action_type
/// and config always mirror the first step so older clients and the server still see one action.
public sealed class VoiceCommand
{
    private const string ActionsKey = "_actions";
    private const string ContextPlaceholder = "{{context}}";

    private Dictionary<string, JsonElement> _actionConfig = new();

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// Pipe-joined alternatives ("call|phone|dial"), lower-cased by the server.
    [JsonPropertyName("trigger_phrase")]
    public string TriggerPhrase { get; set; } = "";

    /// "starts_with", "contains" or "exact". The editor can only create the first two.
    [JsonPropertyName("match_mode")]
    public string MatchMode { get; set; } = "starts_with";

    [JsonPropertyName("action_type")]
    public string ActionType { get; set; } = "";

    [JsonPropertyName("action_config")]
    public Dictionary<string, JsonElement> ActionConfig
    {
        get => _actionConfig;
        set => _actionConfig = value ?? new();
    }

    /// SF Symbol name; see CommandIcons for the Windows glyph.
    [JsonPropertyName("icon_name")]
    public string IconName { get; set; } = "bolt.fill";

    [JsonPropertyName("icon_color")]
    public string IconColor { get; set; } = "#007AFF";

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> AllTriggerPhrases =>
        TriggerPhrase.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// True when a step needs text from the speech ({{context}} in any config value).
    [JsonIgnore]
    public bool UsesContext => AllActions.Any(a => a.ActionConfig.Values.Any(v => VoiceCommandAction.ConfigText(v).Contains(ContextPlaceholder)));

    /// The steps in order: the decoded _actions when present, otherwise one step built from the top level.
    [JsonIgnore]
    public IReadOnlyList<VoiceCommandAction> AllActions
    {
        get
        {
            if (ActionConfig.TryGetValue(ActionsKey, out var packed) && DecodeActions(packed) is { Count: > 0 } decoded)
                return decoded;

            return new[]
            {
                new VoiceCommandAction
                {
                    ActionType = ActionType,
                    ActionConfig = ActionConfig.Where(kv => kv.Key != ActionsKey).ToDictionary(kv => kv.Key, kv => kv.Value),
                },
            };
        }
    }

    /// Top-level action_type and action_config for a list of steps: one step is stored flat, several
    /// are serialized into _actions with the first step's type on top.
    public static (string ActionType, Dictionary<string, JsonElement> ActionConfig) PackActions(IReadOnlyList<VoiceCommandAction> actions)
    {
        if (actions.Count == 0) return ("", new());
        if (actions.Count == 1) return (actions[0].ActionType, actions[0].ActionConfig);

        var json = JsonSerializer.Serialize(actions);
        return (actions[0].ActionType, new() { [ActionsKey] = JsonSerializer.SerializeToElement(json) });
    }

    private static List<VoiceCommandAction>? DecodeActions(JsonElement packed)
    {
        try
        {
            var json = packed.ValueKind == JsonValueKind.String ? packed.GetString() : packed.GetRawText();
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<VoiceCommandAction>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// An action offered in the step picker (GET /coworker/commands/actions). Fields the app does not
/// use (parameters, integration metadata) are ignored.
public sealed class ActionCatalogEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("icon_name")]
    public string IconName { get; set; } = "bolt.fill";

    [JsonPropertyName("icon_color")]
    public string IconColor { get; set; } = "#007AFF";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "builtin";
}

/// Outcome of running a command (server response of /coworker/commands/execute, or built locally).
public sealed class VoiceCommandExecuteResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("icon_name")]
    public string IconName { get; set; } = "bolt.fill";

    [JsonPropertyName("icon_color")]
    public string IconColor { get; set; } = "#007AFF";

    [JsonPropertyName("action_type")]
    public string ActionType { get; set; } = "";
}

/// The editable fields of a command, as sent by the create and update calls.
public sealed record VoiceCommandInput(
    string TriggerPhrase,
    string MatchMode,
    string ActionType,
    Dictionary<string, JsonElement> ActionConfig,
    string IconName,
    string IconColor,
    bool IsEnabled);
