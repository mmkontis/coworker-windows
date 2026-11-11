using System.Text.Json;
using Coworker.Helpers;
using Coworker.Models;

namespace Coworker.Services;

/// The last GET /coworker/models response, kept on disk so the pickers have a real catalog on the next
/// launch with no model table in the app (Mac: ModelCatalogCache in Constants.swift). The server is the only
/// source of model data.
public static class ModelCatalogCache
{
    private static readonly string CachePath = AppPaths.GetPath("model-catalog.json");
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static void Save(IReadOnlyList<TranscriptionModel> models)
    {
        if (models.Count == 0) return;
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(models, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"Model catalog cache not saved: {ex.Message}");
        }
    }

    /// The cached catalog, or an empty list on the very first launch (or an unreadable file).
    public static List<TranscriptionModel> Load()
    {
        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<List<TranscriptionModel>>(File.ReadAllText(CachePath), Options) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write($"Model catalog cache unreadable: {ex.Message}");
            return new();
        }
    }
}
