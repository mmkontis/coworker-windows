namespace Coworker.Paste;

/// Remembers timing and app identity only, never dictated text or keystrokes: three Return presses within
/// two seconds of a confirmed paste, in the same app within a minute, make the app worth an Auto-Enter
/// offer (Mac: AutoSendSuggestionTracker). Times are in milliseconds.
internal sealed class AutoSendSuggestionTracker
{
    private const long ObserveMs = 2_000;
    private const long WindowMs = 60_000;
    private const int SendsNeeded = 3;

    private (string AppId, long Time)? _pending;
    private readonly Dictionary<string, List<long>> _sends = new();

    public void Pasted(string appId, long time)
    {
        Prune(time);
        _pending = (appId, time);
    }

    public void CancelPending() => _pending = null;

    /// True when this Return completes the third send and the offer should be made.
    public bool PressedEnter(string appId, long time)
    {
        var pending = _pending;
        _pending = null;
        Prune(time);

        if (pending is not { } p || p.AppId != appId || time < p.Time || time - p.Time > ObserveMs) return false;

        if (!_sends.TryGetValue(appId, out var times)) _sends[appId] = times = new List<long>();
        times.Add(time);
        if (times.Count < SendsNeeded) return false;

        _sends.Remove(appId);
        return true;
    }

    private void Prune(long time)
    {
        foreach (var appId in _sends.Keys.ToList())
        {
            _sends[appId].RemoveAll(sent => time - sent > WindowMs);
            if (_sends[appId].Count == 0) _sends.Remove(appId);
        }
    }
}
