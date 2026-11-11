using System.Diagnostics;
using System.Text;

namespace Coworker.Settings;

/// Restarts the app so the new process is not turned away by the single-instance mutex: a helper
/// waits until this process is gone (which releases the mutex), then starts Coworker again.
/// AppInstance.Restart() is not used: it ends the process without running the quit path
/// (tray icon, keyboard hook, pending settings write), and the relaunch races the mutex.
public static class AppRestart
{
    /// Runs the normal quit path and relaunches. Returns false, and leaves the app running, when
    /// the helper could not be started.
    public static bool Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            using var helper = Process.Start(CreateHelperStartInfo(exe, Environment.ProcessId));
            if (helper == null) return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        App.Quit();
        return true;
    }

    /// PowerShell waits for the process to exit (at most 30 s) and launches the exe. The script is
    /// passed encoded so the exe path needs no quoting.
    private static ProcessStartInfo CreateHelperStartInfo(string exe, int processId)
    {
        var script = $"Wait-Process -Id {processId} -Timeout 30 -ErrorAction SilentlyContinue; " +
                     $"Start-Process -FilePath '{exe.Replace("'", "''")}'";
        return new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }
}
