using System.Collections.Concurrent;
using FlaUI.UIA3;

namespace Coworker.Paste;

/// One background thread for everything that talks to other apps' accessibility trees. A cross-process
/// call can block for as long as the other app takes to answer, so none of it may run on the UI thread;
/// running it all here also keeps the pastes in order.
internal sealed class UiaWorker
{
    // How long one call into another app may take before it gives up (Mac: AXUIElementSetMessagingTimeout).
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(500);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly object _startGate = new();
    private UIA3Automation? _automation;
    private bool _started;

    /// The automation object; only touch it from a callback that runs on this worker.
    public UIA3Automation Automation => _automation ??= CreateAutomation();

    public Task<T> Run<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    public void Post(Action work) => Enqueue(() =>
    {
        try { work(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.FlaUIException or InvalidOperationException or NullReferenceException)
        {
            Helpers.DebugLog.Write($"UIA task failed: {ex.Message}");
        }
    });

    // The thread starts with the first job, so an app that never dictates never starts it.
    private void Enqueue(Action work)
    {
        lock (_startGate)
        {
            if (!_started)
            {
                _started = true;
                new Thread(() =>
                {
                    foreach (var job in _queue.GetConsumingEnumerable()) job();
                }) { IsBackground = true, Name = "CoworkerUia" }.Start();
            }
        }
        _queue.Add(work);
    }

    private static UIA3Automation CreateAutomation() =>
        new() { ConnectionTimeout = CallTimeout, TransactionTimeout = CallTimeout };
}
