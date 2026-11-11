using System.Text.Json.Serialization;
using Coworker.Models;

namespace Coworker.Services;

// Wire model (Mac CoworkerPlaybook): name, description, system_prompt, trigger_phrases, example_count.
// PlaybookItem.Title = name, .Content = description; .Category is display-only (trigger phrases).
// system_prompt and trigger_phrases are never written from PlaybookItem, so editing a playbook here
// cannot overwrite the prompt set on the Mac/web app.
public partial class ApiService
{
    public async Task<List<PlaybookItem>> GetPlaybooks()
    {
        var response = await _api.GetAsync<PlaybookListResponse>("/coworker/playbooks", ApiRequestOptions.ViaWebProxy);
        return response.Playbooks?.Select(ToItem).ToList() ?? new();
    }

    public async Task<PlaybookItem?> CreatePlaybook(string title, string content, string category)
    {
        var body = new { name = title, description = string.IsNullOrEmpty(content) ? null : content };
        var response = await _api.SendJsonAsync<PlaybookResponse>(HttpMethod.Post, "/coworker/playbooks", body);
        return response.Playbook == null ? null : ToItem(response.Playbook);
    }

    public async Task UpdatePlaybook(string id, string title, string content, string category)
    {
        await _api.SendAsync(HttpMethod.Put, $"/coworker/playbooks/{Uri.EscapeDataString(id)}",
            ApiClient.JsonBody(new { name = title, description = content }));
    }

    public async Task DeletePlaybook(string id)
    {
        await _api.SendAsync(HttpMethod.Delete, $"/coworker/playbooks/{Uri.EscapeDataString(id)}");
    }

    private static PlaybookItem ToItem(PlaybookDto p)
    {
        var triggers = p.TriggerPhrases ?? new List<string>();
        return new PlaybookItem
        {
            Id = p.Id ?? Guid.NewGuid().ToString(),
            Title = p.Name ?? "",
            Content = p.Description ?? "",
            Category = string.Join(", ", triggers),
            SystemPrompt = p.SystemPrompt,
            TriggerPhrases = triggers,
            ExampleCount = p.ExampleCount ?? 0,
            CreatedAt = DateTime.TryParse(p.CreatedAt, out var ca) ? ca : DateTime.Now,
            UpdatedAt = DateTime.TryParse(p.UpdatedAt, out var ua) ? ua : DateTime.Now,
        };
    }

    private record PlaybookListResponse(
        [property: JsonPropertyName("playbooks")] List<PlaybookDto>? Playbooks);

    private record PlaybookResponse(
        [property: JsonPropertyName("playbook")] PlaybookDto? Playbook);

    private record PlaybookDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("system_prompt")] string? SystemPrompt,
        [property: JsonPropertyName("trigger_phrases")] List<string>? TriggerPhrases,
        [property: JsonPropertyName("example_count")] int? ExampleCount,
        [property: JsonPropertyName("created_at")] string? CreatedAt,
        [property: JsonPropertyName("updated_at")] string? UpdatedAt);
}
