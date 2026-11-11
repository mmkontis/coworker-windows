using System.Diagnostics;

namespace Coworker.E2E;

/// <summary>
/// Films the whole desktop with ffmpeg (gdigrab) for the run's artifact, so a failure can be watched. Without
/// ffmpeg on the PATH the test still runs, without a video.
/// </summary>
internal sealed class ScreenRecorder : IDisposable
{
    private readonly Process? _ffmpeg;

    private ScreenRecorder(Process? ffmpeg) => _ffmpeg = ffmpeg;

    public static ScreenRecorder Start(string mp4Path)
    {
        try
        {
            var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-hide_banner -loglevel error -y -f gdigrab -framerate 10 -draw_mouse 1 -i desktop " +
                $"-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{mp4Path}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            });
            Console.WriteLine($"Recording the screen to {mp4Path}");
            return new ScreenRecorder(ffmpeg);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"No screen recording (ffmpeg: {ex.Message})");
            return new ScreenRecorder(null);
        }
    }

    public void Dispose()
    {
        if (_ffmpeg == null) return;
        try
        {
            if (!_ffmpeg.HasExited)
            {
                _ffmpeg.StandardInput.Write('q');   // ffmpeg finishes the file cleanly on "q"
                _ffmpeg.StandardInput.Flush();
                if (!_ffmpeg.WaitForExit(15_000)) _ffmpeg.Kill();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stopping the screen recording: {ex.Message}");
        }
        _ffmpeg.Dispose();
    }
}
