using System.Text.Json.Serialization;

namespace Coworker.Services;

public partial class ApiService
{
    public async Task<DashboardStats> GetDashboardStats()
    {
        try
        {
            var result = await _api.GetAsync<DashboardStatsResponse>("/coworker/dashboard/stats", ApiRequestOptions.ViaWebProxy);
            return new DashboardStats(
                result.TotalWords ?? 0, result.MonthlyWords ?? 0,
                result.TotalTimeSaved ?? 0, result.MonthlyTimeSaved ?? 0,
                result.DictionaryCount ?? 0, result.TestedTypingWpm);
        }
        catch (ApiException ex) when (ex.Kind is ApiErrorKind.Unauthorized or ApiErrorKind.Forbidden)
        {
            throw;
        }
        catch (ApiException)
        {
            // Fall back to building the stats from history, like the server does.
        }

        int totalWords = 0, monthlyWords = 0, weeklyWords = 0, dictCount = 0;

        try
        {
            var history = await GetTranscriptionHistory(5000);
            var now = DateTime.Now;
            var startOfMonth = new DateTime(now.Year, now.Month, 1);
            var startOfWeek = now.Date.AddDays(-(int)now.DayOfWeek);

            foreach (var item in history)
            {
                totalWords += item.WordCount;
                if (item.Timestamp >= startOfMonth) monthlyWords += item.WordCount;
                if (item.Timestamp >= startOfWeek) weeklyWords += item.WordCount;
            }
        }
        catch (ApiException) { }

        try
        {
            dictCount = (await GetDictionaryEntries()).Count;
        }
        catch (ApiException) { }

        const double wpm = 40.0;
        return new DashboardStats(totalWords, monthlyWords, totalWords / (wpm / 60.0), monthlyWords / (wpm / 60.0),
            dictCount, null, weeklyWords, weeklyWords / (wpm / 60.0));
    }

    /// Days are clamped to 1-90 like the Mac.
    public async Task<List<DailyProductivityPoint>> FetchDailyProductivity(int days = 30)
    {
        days = Math.Clamp(days, 1, 90);
        var result = await _api.GetAsync<DailyProductivityResponse>($"/coworker/analytics/daily?days={days}", ApiRequestOptions.ViaWebProxy);

        double typingWpm = result.TestedTypingWpm is > 0 ? result.TestedTypingWpm.Value : 40.0;

        return result.Points?.Select(d =>
        {
            var date = DateTime.TryParse(d.Date, out var dt) ? dt : DateTime.Now;
            var words = d.Words ?? 0;
            var timeSaved = d.TimeSaved ?? (words / typingWpm * 60.0);
            return new DailyProductivityPoint(date, words, timeSaved);
        }).ToList() ?? new();
    }

    private record DashboardStatsResponse(
        [property: JsonPropertyName("total_words")] int? TotalWords,
        [property: JsonPropertyName("monthly_words")] int? MonthlyWords,
        [property: JsonPropertyName("total_time_saved")] double? TotalTimeSaved,
        [property: JsonPropertyName("monthly_time_saved")] double? MonthlyTimeSaved,
        [property: JsonPropertyName("dictionary_count")] int? DictionaryCount,
        [property: JsonPropertyName("tested_typing_wpm")] int? TestedTypingWpm);

    private record DailyProductivityResponse(
        [property: JsonPropertyName("points")] List<DailyProductivityDto>? Points,
        [property: JsonPropertyName("tested_typing_wpm")] int? TestedTypingWpm);

    private record DailyProductivityDto(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("words")] int? Words,
        [property: JsonPropertyName("time_saved")] double? TimeSaved);

    public record DashboardStats(int TotalWords, int MonthlyWords, double TotalTimeSaved,
        double MonthlyTimeSaved, int DictionaryCount, int? TestedTypingWpm,
        int WeeklyWords = 0, double WeeklyTimeSaved = 0);

    public record DailyProductivityPoint(DateTime Date, int Words, double TimeSaved);
}
