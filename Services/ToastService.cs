using Coworker.Controls;
using Coworker.Helpers;
using Coworker.Views;
using Windows.Graphics;

namespace Coworker.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// The app's only toast surface (Mac: CompactNotificationController). Toasts stack down the
/// top-right of the monitor under the cursor, newest first. Safe to call from any thread.
/// </summary>
public static class ToastService
{
    private const double Margin = 12;
    private const double Gap = 9;

    // UI thread only; oldest first.
    private static readonly List<CompactToastWindow> Toasts = new();

    /// <param name="kind">Not drawn (the Mac ignores the toast type); sets screen-reader announcement priority.</param>
    /// <param name="title">Optional bold first line.</param>
    /// <param name="durationSeconds">Auto-dismiss delay; null keeps the toast until dismissed. Mac default is 5.</param>
    /// <returns>An id for <see cref="Dismiss"/>.</returns>
    public static Guid Show(ToastKind kind, string? title, string message, double? durationSeconds = 5,
        string? actionTitle = null, Action? action = null)
    {
        var id = Guid.NewGuid();
        var text = StripStatusEmoji(message);
        UiThread.Post(() => ShowOnUiThread(id, kind, title, text, durationSeconds, actionTitle, action));
        return id;
    }

    public static void Dismiss(Guid id) => UiThread.Post(() =>
    {
        var toast = Toasts.FirstOrDefault(t => t.Id == id);
        if (toast != null) _ = toast.DismissAsync();
    });

    private static void ShowOnUiThread(Guid id, ToastKind kind, string? title, string message, double? durationSeconds,
        string? actionTitle, Action? action)
    {
        var toast = new CompactToastWindow(id, kind, title, message, durationSeconds, actionTitle, action, OnClosed);
        Toasts.Add(toast);

        var (bounds, scale) = Slot(Toasts.Count - 1, Toasts.Count);
        toast.Show(bounds, scale);
        Restack(except: toast);
    }

    private static void OnClosed(CompactToastWindow toast)
    {
        Toasts.Remove(toast);
        Restack(except: null);
    }

    private static void Restack(CompactToastWindow? except)
    {
        for (var i = 0; i < Toasts.Count; i++)
        {
            if (Toasts[i] == except) continue;
            var (bounds, scale) = Slot(i, Toasts.Count);
            Toasts[i].Restack(bounds, scale);
        }
    }

    /// Slot of the toast at <paramref name="index"/> (0 = oldest) among <paramref name="count"/>:
    /// the newest sits at the top.
    private static (RectInt32 Bounds, double Scale) Slot(int index, int count)
    {
        var display = DisplayInfo.FromCursor();
        var scale = display.Scale;
        var work = display.WorkArea;
        var width = (int)Math.Round(CompactToastCard.CardWidth * scale);
        var height = (int)Math.Round(CompactToastCard.CardHeight * scale);
        var fromTop = count - 1 - index;

        var x = work.X + work.Width - width - (int)Math.Round(Margin * scale);
        var y = work.Y + (int)Math.Round((Margin + fromTop * (CompactToastCard.CardHeight + Gap)) * scale);
        return (new RectInt32(x, y, width, height), scale);
    }

    // The Mac strips these leading status emoji before showing a message (CoworkerApp.toastStateChanged).
    private static string StripStatusEmoji(string message)
    {
        foreach (var emoji in new[] { "✅", "❌", "\U0001F534", "⚠️" })
            message = message.Replace(emoji + " ", "").Replace(emoji, "");
        return message.Trim();
    }
}
