using System.Text.Json.Serialization;
using Coworker.Models;

namespace Coworker.Services;

public partial class ApiService
{
    private static readonly ApiRequestOptions Anonymous = new() { Authenticated = false, Timeout = TimeSpan.FromSeconds(10) };

    /// GET /coworker/models (no auth). When the server is unreachable the last cached response is used
    /// (see ModelCatalogCache), so the picker has a real catalog without any model table in the app.
    public async Task<AllModels> GetAllModels()
    {
        try
        {
            var result = await _api.GetAsync<ModelsResponse>("/coworker/models", Anonymous);
            var transcription = ToModels(result.Transcription);
            ModelCatalogCache.Save(transcription);
            return new AllModels(transcription, ToModels(result.Chat));
        }
        catch (ApiException)
        {
            return new AllModels(ModelCatalogCache.Load(), new List<TranscriptionModel>());
        }
    }

    private static List<TranscriptionModel> ToModels(List<ModelDto>? models) =>
        models?.Select(m => new TranscriptionModel
        {
            Code = m.Code ?? "",
            Name = m.Name ?? "",
            Description = m.Description ?? "",
            Tier = m.Tier,
            Featured = m.Featured ?? false,
            Icon = m.Icon,
            ShortName = m.DisplayName,
            Stop = m.Stop,
            StopOrder = m.StopOrder,
            Live = m.Live ?? false,
            PricePerMinuteUsd = m.PricePerMinuteUsd,
            IsDefault = m.Default ?? false,
        }).ToList() ?? new();

    private record ModelsResponse(
        [property: JsonPropertyName("transcription")] List<ModelDto>? Transcription,
        [property: JsonPropertyName("chat")] List<ModelDto>? Chat);

    private record ModelDto(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("tier")] string? Tier,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("featured")] bool? Featured,
        [property: JsonPropertyName("displayName")] string? DisplayName = null,
        [property: JsonPropertyName("stop")] string? Stop = null,
        [property: JsonPropertyName("stopOrder")] int? StopOrder = null,
        [property: JsonPropertyName("live")] bool? Live = null,
        [property: JsonPropertyName("pricePerMinuteUsd")] double? PricePerMinuteUsd = null,
        [property: JsonPropertyName("default")] bool? Default = null);

    public record AllModels(List<TranscriptionModel> Transcription, List<TranscriptionModel> Chat);
}
