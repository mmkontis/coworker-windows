using System.Text.Json.Serialization;
using Coworker.Models;

namespace Coworker.Services;

public partial class ApiService
{
    /// The transcription models of GET /coworker/models plus the code the server marks `default`.
    public sealed record ModelCatalogResult(List<TranscriptionModel> Models, string? DefaultCode);

    /// Same call as <see cref="GetAllModels"/> but strict: throws <see cref="ApiException"/> when the
    /// server is unreachable or lists no usable transcription model, so the caller can retry
    /// instead of silently getting the built-in list (Mac: ContentViewModel.fetchModelsFromServer).
    public async Task<ModelCatalogResult> GetModelCatalogAsync(CancellationToken ct = default)
    {
        var response = await _api.GetAsync<CatalogResponse>("/coworker/models", Anonymous, ct);

        // The Mac skips entries without a code, name or description.
        var entries = (response.Transcription ?? new())
            .Where(m => !string.IsNullOrEmpty(m.Code) && m.Name != null && m.Description != null)
            .ToList();
        if (entries.Count == 0) throw ApiException.InvalidResponse();

        var models = entries.Select(m => new TranscriptionModel
        {
            Code = m.Code!,
            Name = m.Name!,
            Description = m.Description!,
            Tier = m.Tier,
            Featured = m.Featured ?? false,
            Icon = m.Icon,
            ShortName = m.DisplayName,
            Stop = m.Stop,
            StopOrder = m.StopOrder,
            Live = m.Live ?? false,
            PricePerMinuteUsd = m.PricePerMinuteUsd,
            IsDefault = m.Default == true,
        }).ToList();
        ModelCatalogCache.Save(models);
        return new ModelCatalogResult(models, entries.FirstOrDefault(m => m.Default == true)?.Code);
    }

    /// The last catalog the server sent, from disk; empty until the first successful fetch
    /// (Mac: ModelConstants.availableModels).
    public List<TranscriptionModel> BuiltInModels() => ModelCatalogCache.Load();

    private record CatalogResponse(
        [property: JsonPropertyName("transcription")] List<CatalogModelDto>? Transcription);

    private record CatalogModelDto(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("tier")] string? Tier,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("featured")] bool? Featured,
        [property: JsonPropertyName("default")] bool? Default,
        [property: JsonPropertyName("displayName")] string? DisplayName = null,
        [property: JsonPropertyName("stop")] string? Stop = null,
        [property: JsonPropertyName("stopOrder")] int? StopOrder = null,
        [property: JsonPropertyName("live")] bool? Live = null,
        [property: JsonPropertyName("pricePerMinuteUsd")] double? PricePerMinuteUsd = null);
}
