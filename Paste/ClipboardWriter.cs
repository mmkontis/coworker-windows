using System.Runtime.InteropServices;

namespace Coworker.Paste;

/// Puts dictated text on the clipboard through the Win32 clipboard, which works from any thread and
/// whether or not this app has focus. The text stays on the clipboard afterwards (Mac: the same), but is
/// kept out of Windows' clipboard history and cloud clipboard, which would otherwise archive every dictation.
internal static class ClipboardWriter
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int Attempts = 10;

    private static readonly uint HistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CloudFormat = RegisterClipboardFormat("CanUploadToCloudClipboard");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// Replaces the clipboard with <paramref name="text"/>. The clipboard can be held by another app for a
    /// moment, so opening it is retried; false when it never opened.
    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var written = SetData(CF_UNICODETEXT, Encode(text));
                    SetData(HistoryFormat, BitConverter.GetBytes(0));
                    SetData(CloudFormat, BitConverter.GetBytes(0));
                    return written;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(10);
        }
        return false;
    }

    /// The text on the clipboard, or null when there is none or the clipboard would not open.
    public static string? GetText()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = GetClipboardData(CF_UNICODETEXT);
                    if (handle == IntPtr.Zero) return null;

                    var pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(pointer); }
                    finally { GlobalUnlock(handle); }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(10);
        }
        return null;
    }

    private static byte[] Encode(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];   // NUL-terminated UTF-16
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    private static bool SetData(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) return false;

        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(memory);

        // The clipboard owns the memory once SetClipboardData succeeds.
        if (SetClipboardData(format, memory) != IntPtr.Zero) return true;
        GlobalFree(memory);
        return false;
    }
}
