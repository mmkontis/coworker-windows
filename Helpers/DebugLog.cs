namespace Coworker.Helpers;

/// <summary>
/// Lightweight append-only logger for diagnosing the global hotkey / recording
/// bar pipeline. Writes to %LOCALAPPDATA%\Coworker\debug.log.
/// </summary>
public static class DebugLog
{
    private static readonly object Gate = new();

    public static readonly string LogPath = AppPaths.GetPath("debug.log");

    // Logging is OFF unless a "debug.flag" file exists next to settings.json.
    // Keeps the hotkey hook from touching disk on every modifier event in normal use.
    private static readonly bool Enabled = File.Exists(AppPaths.GetPath("debug.flag"));

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
