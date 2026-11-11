using System.ComponentModel;
using System.Diagnostics;

namespace Coworker.Services.VoiceCommands;

/// <param name="Target">What to hand to the shell: a program path or a shell:AppsFolder\ path.</param>
/// <param name="Name">The name shown in results.</param>
public sealed record ResolvedApp(string Target, string Name);

/// Finds and starts the app of an open_app step. Steps carry three keys: bundle_identifier, app_path
/// and app_name.
public static class AppLauncher
{
    /// Looks the app up the way the Mac does (identifier, then path) and adds a name match: a command
    /// authored on the Mac carries a bundle id and a /Applications path that mean nothing here.
    public static ResolvedApp? Resolve(IReadOnlyList<InstalledApp> installed, string bundleId, string appPath, string appName)
    {
        if (bundleId.Length > 0 && installed.FirstOrDefault(a => a.Id.Equals(bundleId, StringComparison.OrdinalIgnoreCase)) is { } byId)
            return new ResolvedApp(byId.ShellPath, appName.Length > 0 ? appName : byId.Name);

        // "Browse…" stores the program path in both keys.
        foreach (var path in new[] { bundleId, appPath })
        {
            if (path.Length > 0 && File.Exists(path))
                return new ResolvedApp(path, appName.Length > 0 ? appName : Path.GetFileNameWithoutExtension(path));
        }

        if (appName.Length > 0 && installed.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase)) is { } byName)
            return new ResolvedApp(byName.ShellPath, appName);

        return null;
    }

    /// Starts the target through the shell; false when the shell refuses.
    public static Task<bool> LaunchAsync(string target) => Task.Run(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    });
}
