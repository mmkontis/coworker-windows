using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace Coworker.Services;

public class ClipboardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = "";
    public string? SourceApp { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ContentType { get; set; } = "text";

    public string Preview => Text.Length > 120 ? Text[..120] + "..." : Text;
    public string TimeAgo
    {
        get
        {
            var span = DateTime.Now - Timestamp;
            if (span.TotalSeconds < 60) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            return Timestamp.ToString("MMM d");
        }
    }
}

public class ClipboardService
{
    public static ClipboardService Shared { get; } = new();
    public ObservableCollection<ClipboardItem> Items { get; } = new();

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Coworker", "clipboard_history.json");

    private readonly System.Timers.Timer _timer;
    private string _lastContent = "";

    private ClipboardService()
    {
        LoadHistory();
        _timer = new System.Timers.Timer(500);
        _timer.Elapsed += (_, _) => CheckClipboard();
    }

    public void StartMonitoring() => _timer.Start();
    public void StopMonitoring() => _timer.Stop();

    private void CheckClipboard()
    {
        try
        {
            var package = Clipboard.GetContent();
            if (!package.Contains(StandardDataFormats.Text)) return;

            var textTask = package.GetTextAsync().AsTask();
            textTask.Wait();
            var text = textTask.Result;

            if (string.IsNullOrEmpty(text) || text == _lastContent) return;
            _lastContent = text;

            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                var existing = Items.FirstOrDefault(i => i.Text == text);
                if (existing != null) Items.Remove(existing);

                Items.Insert(0, new ClipboardItem { Text = text });

                var max = Models.AppSettings.Instance.MaxHistoryItems;
                while (Items.Count > max) Items.RemoveAt(Items.Count - 1);

                SaveHistory();
            });
        }
        catch { }
    }

    public void PasteItem(ClipboardItem item)
    {
        var existing = Items.FirstOrDefault(i => i.Id == item.Id);
        if (existing != null)
        {
            Items.Remove(existing);
            Items.Insert(0, existing);
        }

        var package = new DataPackage();
        package.SetText(item.Text);
        Clipboard.SetContent(package);
        SaveHistory();
    }

    public void RemoveItem(ClipboardItem item)
    {
        Items.Remove(item);
        SaveHistory();
    }

    public void ClearHistory()
    {
        Items.Clear();
        SaveHistory();
    }

    private void SaveHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(Items.ToList());
            File.WriteAllText(HistoryPath, json);
        }
        catch { }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var json = File.ReadAllText(HistoryPath);
            var items = JsonSerializer.Deserialize<List<ClipboardItem>>(json);
            if (items == null) return;
            foreach (var item in items) Items.Add(item);
        }
        catch { }
    }
}
