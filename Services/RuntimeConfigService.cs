using Coworker.Helpers;
using Coworker.Models;

namespace Coworker.Services;

/// <summary>
/// The admin-managed defaults from GET /coworker/runtime-config, applied to settings the user has not
/// chosen themselves (Mac: RuntimeConfigManager). Advisory: when the server cannot be reached the app
/// simply keeps its own values.
/// </summary>
public sealed class RuntimeConfigService
{
    public static RuntimeConfigService Shared { get; } = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly string[] AudioQualityPresets = { "compact_16k", "standard_24k", "high_44k", "hardware" };

    private DateTime? _lastFetch;

    private RuntimeConfigService() { }

    /// Raised on the UI thread after a config was applied, so a visible model picker can follow it.
    public event Action? Updated;

    /// Fetches and applies the config unless one was fetched in the last five minutes.
    public async Task FetchAndApplyOnLaunchAsync()
    {
        if (_lastFetch is { } last && DateTime.UtcNow - last < CacheTtl) return;
        await FetchAndApplyAsync();
    }

    public async Task FetchAndApplyAsync()
    {
        try
        {
            var config = await ApiService.Shared.GetRuntimeConfig();
            _lastFetch = DateTime.UtcNow;
            Apply(config);
            DebugLog.Write($"Runtime config v{config.Version} applied (preset={config.AudioQualityPreset}, model={config.DefaultTranscriptionModel})");
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Runtime config not fetched: {ex.Kind}");
        }
    }

    // Each remote-managed field is written only while the user has not picked their own value.
    private void Apply(ApiService.RuntimeConfig config)
    {
        var settings = AppSettings.Instance;

        if (!settings.UserOverrodeAudioQualityPreset && config.AudioQualityPreset is { } preset && AudioQualityPresets.Contains(preset))
            settings.AudioQualityPreset = preset;

        if (!settings.UserOverrodeSelectedModel && config.DefaultTranscriptionModel?.Trim() is { Length: > 0 } model)
            settings.SelectedModel = model;

        settings.RuntimeConfigLastFetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        settings.RuntimeConfigLastVersion = config.Version;
        settings.Save();
        Updated?.Invoke();
    }
}
