namespace Coworker.Helpers;

public static class AppPaths
{
    /// %LOCALAPPDATA%\Coworker, or the folder in COWORKER_DATA_DIR (dev/test isolation).
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string GetPath(string name) => Path.Combine(DataDirectory, name);

    private static string ResolveDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("COWORKER_DATA_DIR")?.Trim();
        var dir = string.IsNullOrEmpty(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coworker")
            : overrideDir;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
