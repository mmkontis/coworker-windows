namespace Coworker.Models;

// Clipboard history, paste and Auto-Enter. Defaults follow the Mac (ClipboardFeature/AppSettings.swift).
public partial class AppSettings
{
    public bool ClipboardHistoryEnabled { get; set; }
    public const string DefaultClipboardShortcut = "Ctrl+Shift+V";
    public string ClipboardShortcut { get; set; } = DefaultClipboardShortcut;
    public int MaxHistoryItems { get; set; } = 100;
    public bool ClearHistoryOnQuit { get; set; }
    public bool ShowMediaBar { get; set; }
    public bool DismissRecentOnPaste { get; set; } = true;

    public bool AutoPaste { get; set; } = true;
    public bool AutoSubmitEnabled { get; set; }
    public List<string> AutoSubmitApps { get; set; } = new();
    public List<string> AutoSubmitSites { get; set; } = new();
}
