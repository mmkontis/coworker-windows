using System.Text.Json.Serialization;
using Coworker.Models;

namespace Coworker.Services;

public partial class ApiService
{
    /// Node only (the Mac does not use the web proxy for history).
    public async Task<List<TranscriptionHistoryItem>> GetTranscriptionHistory(int limit = 100, int offset = 0)
    {
        var userId = SessionService.Shared.UserId;
        if (string.IsNullOrEmpty(userId)) userId = (await GetUserMe()).Id;

        var path = $"/coworker/transcriptions/history/{Uri.EscapeDataString(userId)}?limit={limit}&offset={offset}";
        var response = await _api.GetAsync<TranscriptionHistoryResponse>(path);

        return response.Transcriptions?.Select(t => new TranscriptionHistoryItem
        {
            Id = t.Id ?? "",
            Text = t.Result ?? "",
            Timestamp = DateTime.TryParse(t.CreatedAt, out var dt) ? dt : DateTime.Now,
            WordCount = t.WordCount ?? t.Result?.Split(' ').Length ?? 0,
            Duration = t.Duration ?? 0,
            App = t.ActiveApp,
            Language = t.Language,
            Model = t.Model,
        }).Where(t => !string.IsNullOrEmpty(t.Text)).ToList() ?? new();
    }

    private record TranscriptionHistoryResponse(
        [property: JsonPropertyName("transcriptions")] List<TranscriptionDto>? Transcriptions);

    private record TranscriptionDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("created_at")] string? CreatedAt,
        [property: JsonPropertyName("word_count")] int? WordCount,
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("active_app")] string? ActiveApp,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("model")] string? Model);
}
