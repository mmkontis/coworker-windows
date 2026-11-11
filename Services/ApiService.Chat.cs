using System.Text.Json.Serialization;
using Coworker.Models;

namespace Coworker.Services;

// The chat-completion voice mode, plus the legacy endpoints behind the existing Chat page (typed chat
// stays outside the port scope).
public partial class ApiService
{
    private static readonly ApiRequestOptions ChatStream = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        Completion = HttpCompletionOption.ResponseHeadersRead,
    };

    public async IAsyncEnumerable<string> ChatCompletionStream(string message, string model, List<ChatMessage>? context = null)
    {
        object messagesPayload = context is { Count: > 0 }
            ? context.Select(m => new { role = m.Role, content = m.Content }).ToList()
            : new List<object> { new { role = "user", content = message } };

        using var response = await _api.SendRawAsync(HttpMethod.Post, "/coworker/chat/stream",
            ApiClient.JsonBody(new { messages = messagesPayload, model, stream = true }), ChatStream);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            yield return data;
        }
    }

    private static readonly ApiRequestOptions VoiceChat = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// Chat-completion voice mode (Mac: the chat_mode transcription path): the spoken question in, the
    /// AI's answer out, for pasting. One message with no history, as on the Mac, and no model: the server
    /// uses its default chat model. voice_take: one turn with no tools and no thinking, as on the Mac.
    /// POST /coworker/chat.
    public async Task<string> VoiceChatAsync(string question, string instructions, CancellationToken ct = default)
    {
        var body = new
        {
            device_id = GetDeviceId(),
            request_id = Guid.NewGuid().ToString().ToUpperInvariant(),
            message = question,
            context = instructions,
            enable_functions = false,
            voice_take = true,
        };
        var reply = await _api.SendJsonAsync<VoiceChatReply>(HttpMethod.Post, "/coworker/chat", body, VoiceChat, ct);
        return reply.Response?.Trim() ?? "";
    }

    private record VoiceChatReply([property: JsonPropertyName("response")] string? Response);

    public async Task<List<(string Code, string Name)>> GetChatModels()
    {
        var all = await GetAllModels();
        return all.Chat.Select(m => (m.Code, m.Name)).ToList();
    }

    /// GET /coworker/contexts: workspaces + companies for the Chat page's scope dropdowns.
    public async Task<CoworkerContexts> FetchUserContexts()
    {
        var result = await _api.GetAsync<ContextsResponse>("/coworker/contexts", ApiRequestOptions.ViaWebProxy);
        return new CoworkerContexts
        {
            Workspaces = result.Workspaces?.Select(w => new CoworkerWorkspace
            {
                Id = w.Id ?? "",
                Name = w.Name ?? "",
                Kind = w.Kind,
            }).ToList() ?? new(),
            Companies = result.Companies?.Select(c => new CoworkerCompany
            {
                Id = c.Id ?? "",
                Name = c.Name ?? "",
                Slug = c.Slug,
                LogoUrl = c.LogoUrl,
            }).ToList() ?? new(),
            ActiveCompanyId = result.ActiveCompanyId,
        };
    }

    private record ContextsResponse(
        [property: JsonPropertyName("workspaces")] List<WorkspaceDto>? Workspaces,
        [property: JsonPropertyName("companies")] List<CompanyDto>? Companies,
        [property: JsonPropertyName("active_company_id")] string? ActiveCompanyId);

    private record WorkspaceDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("kind")] string? Kind);

    private record CompanyDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("logo_url")] string? LogoUrl);
}
