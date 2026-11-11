using System.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Coworker.Settings;

/// Finds the icon of an app known only by the name the clipboard recorded (its process name or its
/// file description), by looking for a running process with that name. Apps that are not running
/// get no icon and the caller shows a placeholder.
public static class AppIconResolver
{
    private static readonly Dictionary<string, string> ExecutablesByName = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _scannedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// Call from the UI thread; the process scan runs in the background.
    public static async Task<BitmapImage?> GetIconAsync(string appName)
    {
        try
        {
            var path = await FindExecutableAsync(appName);
            if (path == null) return null;

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 56, ThumbnailOptions.UseCurrentScale);
            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static async Task<string?> FindExecutableAsync(string appName)
    {
        await Gate.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _scannedAt > TimeSpan.FromSeconds(30))
            {
                await Task.Run(Scan);
                _scannedAt = DateTime.UtcNow;
            }
            return ExecutablesByName.TryGetValue(appName, out var path) ? path : null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Scan()
    {
        ExecutablesByName.Clear();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var module = process.MainModule;
                    if (module == null) continue;

                    ExecutablesByName.TryAdd(process.ProcessName, module.FileName);
                    if (module.FileVersionInfo.FileDescription is { Length: > 0 } description)
                        ExecutablesByName.TryAdd(description, module.FileName);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected or exited process: no icon for it.
                }
            }
        }
    }
}
