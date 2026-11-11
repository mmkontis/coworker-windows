using System.Text.Json.Serialization;
using Coworker.Models;

namespace Coworker.Services;

// Voice commands (Mac: "Voice Commands CRUD" in APIService.swift). Payloads: Humanlike-node
// plugins/coworker/routes.ts and voice-commands.ts.
public partial class ApiService
{
    /// Every command of the user, disabled ones included.
    public async Task<List<VoiceCommand>> GetVoiceCommands(CancellationToken ct = default)
    {
        var response = await _api.GetAsync<VoiceCommandsResponse>("/coworker/commands", ApiRequestOptions.ViaWebProxy, ct);
        return response.Commands ?? new();
    }

    public async Task<VoiceCommand> CreateVoiceCommand(VoiceCommandInput input)
    {
        var body = new Dictionary<string, object>
        {
            ["trigger_phrase"] = input.TriggerPhrase,
            ["match_mode"] = input.MatchMode,
            ["action_type"] = input.ActionType,
            ["icon_name"] = input.IconName,
            ["icon_color"] = input.IconColor,
            ["is_enabled"] = input.IsEnabled,
            ["sort_order"] = 0,
        };
        if (input.ActionConfig.Count > 0) body["action_config"] = input.ActionConfig;

        var response = await _api.SendJsonAsync<SingleVoiceCommandResponse>(HttpMethod.Post, "/coworker/commands", body);
        return response.Command ?? throw ApiException.InvalidResponse();
    }

    public async Task<VoiceCommand> UpdateVoiceCommand(string id, VoiceCommandInput input)
    {
        var body = new Dictionary<string, object>
        {
            ["trigger_phrase"] = input.TriggerPhrase,
            ["match_mode"] = input.MatchMode,
            ["action_type"] = input.ActionType,
            ["action_config"] = input.ActionConfig,
            ["icon_name"] = input.IconName,
            ["icon_color"] = input.IconColor,
            ["is_enabled"] = input.IsEnabled,
        };

        var response = await _api.SendJsonAsync<SingleVoiceCommandResponse>(
            HttpMethod.Put, $"/coworker/commands/{Uri.EscapeDataString(id)}", body);
        return response.Command ?? throw ApiException.InvalidResponse();
    }

    public async Task DeleteVoiceCommand(string id)
    {
        await _api.SendAsync(HttpMethod.Delete, $"/coworker/commands/{Uri.EscapeDataString(id)}");
    }

    /// Runs the command's top-level action on the server. The server re-extracts the spoken
    /// argument from <paramref name="transcribedText"/> itself.
    public async Task<VoiceCommandExecuteResult> ExecuteVoiceCommand(string commandId, string transcribedText)
    {
        return await _api.SendJsonAsync<VoiceCommandExecuteResult>(HttpMethod.Post, "/coworker/commands/execute",
            new { command_id = commandId, transcribed_text = transcribedText });
    }

    public async Task<List<ActionCatalogEntry>> GetActionsCatalog()
    {
        var response = await _api.GetAsync<ActionsCatalogResponse>("/coworker/commands/actions", ApiRequestOptions.ViaWebProxy);
        return response.Actions ?? new();
    }

    private record VoiceCommandsResponse(
        [property: JsonPropertyName("commands")] List<VoiceCommand>? Commands);

    private record SingleVoiceCommandResponse(
        [property: JsonPropertyName("command")] VoiceCommand? Command);

    private record ActionsCatalogResponse(
        [property: JsonPropertyName("actions")] List<ActionCatalogEntry>? Actions);
}
