using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Pages;

public sealed partial class DashboardPage : Page
{
    private int _rangeDays = AppSettings.Instance.DashboardRangeDays == 30 ? 30 : 7;
    private int _request;   // newest load; an older one finishing late is ignored

    public DashboardPage()
    {
        InitializeComponent();
        LoadActions();
        UpdateRangeButtons();
        Loaded += (_, _) => FetchMetrics();
    }

    // Words and time saved for the chosen period (only those days are read, not the whole history),
    // with the dictionary and playbook counts. Skeletons stand in until all of it has arrived.
    private async void FetchMetrics()
    {
        var request = ++_request;
        var days = _rangeDays;

        if (string.IsNullOrEmpty(AppSettings.Instance.AccessToken))
        {
            ShowMetrics(null, "Sign in to see your stats");
            return;
        }

        ShowMetrics(null);
        try
        {
            var dailyTask = ApiService.Shared.FetchDailyProductivity(days);
            var playbooksTask = ApiService.Shared.GetPlaybooks();
            var dictionaryTask = ApiService.Shared.GetDictionaryEntries();
            await Task.WhenAll(dailyTask, playbooksTask, dictionaryTask);
            if (request != _request) return;

            var daily = dailyTask.Result;
            ShowMetrics(new Totals(daily.Sum(p => p.Words), daily.Sum(p => p.TimeSaved),
                dictionaryTask.Result.Count, playbooksTask.Result.Count));
        }
        catch (Exception ex)
        {
            if (request == _request) ShowMetrics(null, $"Couldn't load: {ex.Message}");
        }
    }

    private sealed record Totals(int Words, double TimeSaved, int DictionaryCount, int PlaybookCount);

    /// <param name="totals">Null while loading (skeletons) or when <paramref name="problem"/> says why.</param>
    private void ShowMetrics(Totals? totals, string? problem = null)
    {
        var loading = totals == null && problem == null;
        var period = _rangeDays == 30 ? "Last 30 days" : "Last 7 days";
        var wpm = AppSettings.Instance.TestedTypingWpm is > 0 and var tested ? tested : 40;

        MetricViewModel Metric(string icon, string label, string value, string helper, string tag) =>
            new(icon, label, value, problem ?? helper, tag, loading);

        MetricsRepeater.ItemsSource = new[]
        {
            Metric("\uE8F2", "WORDS", totals?.Words.ToString("N0") ?? "—", period, "Analytics"),
            Metric("\uE823", "TIME SAVED", totals != null ? FormatTime(totals.TimeSaved) : "—", $"{period} at {wpm} WPM", "Analytics"),
            Metric("\uE82D", "DICTIONARY", totals?.DictionaryCount.ToString() ?? "—", "Custom entries", "Dictionary"),
            Metric("\uE8A5", "PLAYBOOKS", totals?.PlaybookCount.ToString() ?? "—", "Prompt templates", "Playbooks"),
        };
    }

    private void OnRangeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var days) || days == _rangeDays) return;
        _rangeDays = days;
        AppSettings.Instance.DashboardRangeDays = days;
        AppSettings.Instance.Save();
        UpdateRangeButtons();
        FetchMetrics();
    }

    // The selected period in the accent colour, the other plain (as on the Analytics page).
    private void UpdateRangeButtons()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["CwAccentBrush"];
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var secondary = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        foreach (var (button, days) in new[] { (Range7D, 7), (Range30D, 30) })
        {
            var selected = days == _rangeDays;
            button.Background = selected ? accent : clear;
            button.Foreground = selected ? white : secondary;
        }
    }

    private void LoadActions()
    {
        var actions = new[]
        {
            new ActionViewModel("\uE710", "Add Entry", "New dictionary item", "Dictionary"),
            new ActionViewModel("\uE77B", "Contacts", "Manage people", "Contacts"),
            new ActionViewModel("\uE72C", "Sync Contacts", "Refresh system contacts", "SyncContacts"),
            new ActionViewModel("\uE713", "Settings", "App configuration", "Settings"),
        };
        ActionsRepeater.ItemsSource = actions;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        FetchMetrics();
    }

    private void OnMetricClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var mainWindow = App.MainWindow as MainWindow;
            if (Enum.TryParse<AppTab>(tag, out var tab))
            {
                mainWindow?.NavigateTo(tab);
            }
        }
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var mainWindow = App.MainWindow as MainWindow;
            if (Enum.TryParse<AppTab>(tag, out var tab))
            {
                mainWindow?.NavigateTo(tab);
            }
        }
    }

    private static string FormatTime(double seconds)
    {
        var hours = (int)seconds / 3600;
        var minutes = (int)seconds / 60 % 60;
        var secs = (int)seconds % 60;

        if (hours > 0) return $"{hours}h {minutes}m";
        if (minutes > 0) return $"{minutes}m {secs}s";
        return $"{secs}s";
    }
}

public record MetricViewModel(string Icon, string Label, string Value, string Helper, string? Tag = null, bool Loading = false)
{
    public Visibility ValueVisibility => Loading ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SkeletonVisibility => Loading ? Visibility.Visible : Visibility.Collapsed;
}
public record ActionViewModel(string Icon, string Title, string Description, string Tag);
