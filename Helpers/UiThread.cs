using Microsoft.UI.Dispatching;

namespace Coworker.Helpers;

/// Marshals work onto the UI thread from services that run on background threads.
public static class UiThread
{
    private static DispatcherQueue? _queue;

    /// Call once on the UI thread at startup.
    public static void Initialize() => _queue = DispatcherQueue.GetForCurrentThread();

    public static DispatcherQueue Queue =>
        _queue ?? throw new InvalidOperationException("UiThread.Initialize() has not run.");

    /// Runs inline when already on the UI thread, otherwise queues. Drops the work if the app is shutting down.
    public static void Post(Action action)
    {
        var queue = _queue;
        if (queue == null) return;
        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }
}
