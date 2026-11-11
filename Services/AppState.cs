using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Coworker.Helpers;
using Coworker.Models;

namespace Coworker.Services;

/// <summary>
/// State shared across pages so switching tabs does not refetch (Mac: the state owned by AppView):
/// dashboard stats, dictionary entries, playbook count. Listens for <see cref="DictionaryChangedMessage"/>,
/// <see cref="StatsChangedMessage"/> and <see cref="SessionInvalidatedMessage"/>. UI thread only.
/// </summary>
public sealed class AppState : ObservableObject
{
    public static AppState Shared { get; } = new();

    private ApiService.DashboardStats? _stats;
    private int _playbookCount;
    private bool _isRefreshing;

    private AppState()
    {
        WeakReferenceMessenger.Default.Register<DictionaryChangedMessage>(this, (_, _) => UiThread.Post(() => _ = RefreshDictionaryAsync()));
        WeakReferenceMessenger.Default.Register<StatsChangedMessage>(this, (_, _) => UiThread.Post(() => _ = RefreshStatsAsync()));
        WeakReferenceMessenger.Default.Register<SessionInvalidatedMessage>(this, (_, _) => UiThread.Post(Clear));
    }

    public ApiService.DashboardStats? Stats
    {
        get => _stats;
        private set => SetProperty(ref _stats, value);
    }

    public int PlaybookCount
    {
        get => _playbookCount;
        private set => SetProperty(ref _playbookCount, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public ObservableCollection<DictionaryEntry> DictionaryEntries { get; } = new();

    /// Reloads stats and the playbook count. Errors leave the previous values in place.
    public async Task RefreshStatsAsync()
    {
        if (IsRefreshing || !SessionService.Shared.IsAuthenticated) return;
        IsRefreshing = true;
        try
        {
            var stats = ApiService.Shared.GetDashboardStats();
            var playbooks = ApiService.Shared.GetPlaybooks();
            await Task.WhenAll(stats, playbooks);
            Stats = stats.Result;
            PlaybookCount = playbooks.Result.Count;
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"AppState: stats refresh failed ({ex.Kind})");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task RefreshDictionaryAsync()
    {
        if (!SessionService.Shared.IsAuthenticated) return;
        try
        {
            var entries = await ApiService.Shared.GetDictionaryEntries();
            DictionaryEntries.Clear();
            foreach (var entry in entries) DictionaryEntries.Add(entry);
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"AppState: dictionary refresh failed ({ex.Kind})");
        }
    }

    private void Clear()
    {
        Stats = null;
        PlaybookCount = 0;
        DictionaryEntries.Clear();
    }
}
