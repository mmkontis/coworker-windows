using System.Text.Json.Serialization;
using Coworker.Models;

namespace Coworker.Services;

public partial class ApiService
{
    public async Task<List<DictionaryEntry>> GetDictionaryEntries()
    {
        var response = await _api.GetAsync<DictionaryListResponse>("/coworker/words", ApiRequestOptions.ViaWebProxy);
        return (response.Entries ?? response.Words)?.Select(ToEntry).ToList() ?? new();
    }

    /// Returns the created entry (with its server id) so callers can offer Undo.
    public async Task<DictionaryEntry?> AddDictionaryEntry(string word, string definition, string? shortcut)
    {
        var response = await _api.SendJsonAsync<DictionaryWordResponse>(
            HttpMethod.Post, "/coworker/words", new { word, definition, shortcut });
        return response.Word == null ? null : ToEntry(response.Word);
    }

    public async Task UpdateDictionaryEntry(string id, string word, string definition, string? shortcut)
    {
        await _api.SendAsync(HttpMethod.Put, $"/coworker/words/{Uri.EscapeDataString(id)}",
            ApiClient.JsonBody(new { word, definition, shortcut }));
    }

    public async Task DeleteDictionaryEntry(string id)
    {
        await _api.SendAsync(HttpMethod.Delete, $"/coworker/words/{Uri.EscapeDataString(id)}");
    }

    private static DictionaryEntry ToEntry(DictionaryEntryDto e)
    {
        var word = e.Word ?? "";
        return new DictionaryEntry
        {
            Id = e.Id ?? Guid.NewGuid().ToString(),
            Word = word,
            Definition = e.Definition ?? word,
            Shortcut = e.Shortcut,
        };
    }

    private record DictionaryListResponse(
        [property: JsonPropertyName("entries")] List<DictionaryEntryDto>? Entries,
        [property: JsonPropertyName("words")] List<DictionaryEntryDto>? Words);

    private record DictionaryWordResponse(
        [property: JsonPropertyName("word")] DictionaryEntryDto? Word);

    private record DictionaryEntryDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("word")] string? Word,
        [property: JsonPropertyName("definition")] string? Definition,
        [property: JsonPropertyName("shortcut")] string? Shortcut);
}
