namespace Coworker.Transcription;

/// How long a take may wait for its answer before it is retried over HTTP (Mac: timeoutBudgetMs).
public static class AdaptiveTimeout
{
    private const double MinMs = 6_000;
    private const double MaxMs = 30_000;

    /// 6 to 30 seconds: 1.5 s plus 1.8x the recording, or 1.25x the recent 95th-percentile latency, whichever is longer.
    public static double BudgetMs(double recordedDurationMs, double recentSuccessfulP95Ms)
    {
        var dynamicFloor = Math.Max(MinMs, Math.Max(1_500 + Math.Max(recordedDurationMs, 0) * 1.8, Math.Max(recentSuccessfulP95Ms, 0) * 1.25));
        return Math.Min(Math.Max(dynamicFloor, MinMs), MaxMs);
    }
}

/// The last 20 successful response latencies, for the adaptive timeout.
public sealed class LatencyTracker
{
    private const int Capacity = 20;

    private readonly List<double> _samples = new();

    public void Add(double milliseconds)
    {
        _samples.Add(milliseconds);
        if (_samples.Count > Capacity) _samples.RemoveAt(0);
    }

    public double P95Ms
    {
        get
        {
            if (_samples.Count == 0) return 0;
            var sorted = _samples.Order().ToList();
            return sorted[Math.Min(sorted.Count - 1, (int)((sorted.Count - 1) * 0.95))];
        }
    }
}
