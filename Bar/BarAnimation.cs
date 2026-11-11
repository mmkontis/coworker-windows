namespace Coworker.Bar;

/// SwiftUI's animation curves, evaluated by hand (the bar is drawn, not a XAML tree).
internal static class BarEasing
{
    /// easeInOut: cubic-bezier(0.42, 0, 0.58, 1), the curve behind .easeInOut(duration:).
    public static double EaseInOut(double t) => CubicBezier(0.42, 0.0, 0.58, 1.0, t);

    private static double CubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;

        // Solve for the curve parameter whose x equals the input, then read y.
        double lo = 0, hi = 1, t = x;
        for (var i = 0; i < 24; i++)
        {
            t = (lo + hi) / 2;
            var current = Sample(x1, x2, t);
            if (Math.Abs(current - x) < 1e-5) break;
            if (current < x) lo = t; else hi = t;
        }
        return Sample(y1, y2, t);
    }

    private static double Sample(double p1, double p2, double t)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }
}

/// A value that glides to each new target with easeInOut, restarting from wherever it is when the target
/// changes (what .animation(.easeInOut(duration:), value:) does).
internal sealed class EasedValue
{
    private readonly double _defaultDurationMs;
    private double _durationMs;
    private double _from;
    private double _to;
    private long _startedAt;

    public EasedValue(double initial, double durationMs)
    {
        _from = _to = initial;
        _defaultDurationMs = _durationMs = durationMs;
    }

    public double At(long nowMs)
    {
        if (nowMs >= _startedAt + _durationMs) return _to;
        return _from + (_to - _from) * BarEasing.EaseInOut((nowMs - _startedAt) / _durationMs);
    }

    public bool IsMoving(long nowMs) => nowMs < _startedAt + _durationMs && _from != _to;

    public void MoveTo(double target, long nowMs, double? durationMs = null)
    {
        if (target == _to) return;
        _from = At(nowMs);
        _to = target;
        _startedAt = nowMs;
        _durationMs = durationMs ?? _defaultDurationMs;
    }

    /// Jumps to a value with no animation.
    public void Set(double value) => _from = _to = value;
}
