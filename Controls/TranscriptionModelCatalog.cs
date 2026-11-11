using System.Text.RegularExpressions;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Controls;

/// <summary>
/// The transcription models shared by Settings and the other model pickers: the server catalog
/// (GET /coworker/models) with the last cached response as fallback, the slider stops, the models
/// the user added from OpenRouter, and how a pick is persisted (Mac: TranscriptionModelCatalog in
/// TranscriptionModelPicker.swift and fetchModelsFromServer in CoworkerApp.swift). UI thread only.
/// </summary>
public sealed class TranscriptionModelCatalog
{
    public static TranscriptionModelCatalog Shared { get; } = new();

    /// The slider positions, left to right: label and model code. They come from the catalog the server last
    /// sent (each model's `stop` and `stopOrder`); only before the catalog has ever loaded (first launch, offline)
    /// does the slider use <see cref="BootstrapStops"/>, because it needs two or more fixed positions to lay out.
    public static IReadOnlyList<(string Label, string Code)> Stops => Shared.CurrentStops();

    private static readonly (string Label, string Code)[] BootstrapStops =
    {
        ("Groq", "groq-whisper-v3-turbo"),
        ("GPT", "gpt-transcribe"),
        ("Qwen", "nari-qwen3-asr-fast"),
        ("MAI", "microsoft/mai-transcribe-2"),
    };

    /// The model the personal OpenRouter key runs, which a stale server catalog must not hide.
    private const string PersonalOpenRouterModel = "microsoft/mai-transcribe-2";

    private IReadOnlyList<(string Label, string Code)> CurrentStops()
    {
        var stops = _serverModels
            .Where(m => m.Stop != null)
            .OrderBy(m => m.StopOrder ?? int.MaxValue)
            .Select(m => (m.Stop!, m.Code))
            .ToList();
        return stops.Count >= 2 ? stops : BootstrapStops;
    }

    private const string CustomModelDescription = "OpenRouter transcription";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private static readonly Regex OpenRouterModelId =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.Compiled);

    private List<TranscriptionModel> _serverModels;
    private bool _refreshStarted;

    private TranscriptionModelCatalog()
    {
        _serverModels = ApiService.Shared.BuiltInModels();
    }

    /// Raised on the UI thread after the server catalog arrived.
    public event Action? Changed;

    /// Starts the first fetch of the server catalog; later calls do nothing.
    public void EnsureRefreshed()
    {
        if (_refreshStarted) return;
        _refreshStarted = true;
        _ = RefreshAsync();
    }

    /// Up to three attempts, three seconds apart. On failure the built-in list stays. When the
    /// saved model is not in the new list the server's default takes its place.
    public async Task RefreshAsync()
    {
        ApiService.ModelCatalogResult? result = null;
        for (var attempt = 0; attempt < 3 && result == null; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryDelay);
            try
            {
                result = await ApiService.Shared.GetModelCatalogAsync();
            }
            catch (ApiException ex)
            {
                DebugLog.Write($"Models fetch failed (attempt {attempt + 1}): {ex.Message}");
            }
        }
        if (result == null) return;

        UiThread.Post(() => Apply(result));
    }

    /// Everything a picker lists, in the server's order: the catalog plus any slider model the server dropped,
    /// the user's OpenRouter models, and a placeholder for the saved model when no catalog has loaded yet.
    public IReadOnlyList<TranscriptionModel> Choices()
    {
        var all = new List<TranscriptionModel>(_serverModels);
        foreach (var model in ApiService.Shared.BuiltInModels())
        {
            if (model.Stop != null && all.All(m => m.Code != model.Code)) all.Add(model);
        }

        foreach (var custom in CustomModels())
        {
            if (all.All(m => m.Code != custom.Id))
                all.Add(new TranscriptionModel { Code = custom.Id, Name = custom.Name, Description = CustomModelDescription });
        }

        var selected = AppSettings.Instance.SelectedModel;
        if (all.Count == 0 && !string.IsNullOrEmpty(selected))
            all.Add(new TranscriptionModel { Code = selected, Name = selected, Description = "Loading models…" });
        return all;
    }

    public static string DisplayName(TranscriptionModel model) => model.ShortName ?? model.Name;

    /// Saves a model the user picked, so a server default no longer replaces it.
    public static void Select(string code)
    {
        var settings = AppSettings.Instance;
        settings.SelectedModel = code;
        settings.UserOverrodeSelectedModel = true;
        settings.Save();
    }

    /// The saved OpenRouter models, without entries that are malformed or repeated.
    private static IReadOnlyList<AppSettings.OpenRouterModel> CustomModels()
    {
        var ids = new HashSet<string>();
        return AppSettings.Instance.CustomOpenRouterTranscriptionModels
            .Where(m => OpenRouterModelId.IsMatch(m.Id) && !string.IsNullOrEmpty(m.Name) && ids.Add(m.Id))
            .ToList();
    }

    private void Apply(ApiService.ModelCatalogResult result)
    {
        var visible = new List<TranscriptionModel>(result.Models);

        // A stale server catalog must not hide the model the personal OpenRouter key runs.
        var settings = AppSettings.Instance;
        var mai = PersonalOpenRouterModel;
        if (settings.OpenRouterDirectTranscriptionEnabled && visible.All(m => m.Code != mai) &&
            ApiService.Shared.BuiltInModels().FirstOrDefault(m => m.Code == mai) is { } builtIn)
        {
            visible.Insert(0, builtIn);
        }
        _serverModels = visible;

        if (Choices().All(m => m.Code != settings.SelectedModel))
        {
            settings.SelectedModel = result.Models.FirstOrDefault(m => m.Code == result.DefaultCode)?.Code
                ?? result.Models[0].Code;
            settings.Save();
        }
        Changed?.Invoke();
    }
}
