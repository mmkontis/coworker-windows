namespace Coworker.Helpers;

/// <summary>
/// Message hooks on an existing window via SetWindowSubclass (works on x86, x64 and ARM64). Several
/// hooks may listen to the same message; the first one that reports it handled ends the chain.
/// </summary>
internal sealed class WindowSubclass : IDisposable
{
    /// Return true (and set <paramref name="result"/>) to consume the message.
    public delegate bool MessageHook(IntPtr wParam, IntPtr lParam, out IntPtr result);

    private static int _nextId;

    private readonly IntPtr _hwnd;
    private readonly UIntPtr _id = (UIntPtr)(uint)Interlocked.Increment(ref _nextId);
    private readonly NativeMethods.SubclassProc _proc;   // held so the delegate is not collected
    private readonly Dictionary<uint, List<MessageHook>> _hooks = new();
    private bool _disposed;

    public WindowSubclass(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = Proc;
        NativeMethods.SetWindowSubclass(hwnd, _proc, _id, UIntPtr.Zero);
    }

    public void Add(uint msg, MessageHook hook)
    {
        if (!_hooks.TryGetValue(msg, out var list)) _hooks[msg] = list = new List<MessageHook>();
        list.Add(hook);
    }

    private IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
    {
        if (_hooks.TryGetValue(msg, out var list))
        {
            foreach (var hook in list)
            {
                if (hook(wParam, lParam, out var result)) return result;
            }
        }

        if (msg == NativeMethods.WM_NCDESTROY) Dispose();
        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.RemoveWindowSubclass(_hwnd, _proc, _id);
    }
}
