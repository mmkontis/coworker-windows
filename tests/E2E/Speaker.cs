using NAudio.Wave;

namespace Coworker.E2E;

/// <summary>
/// Plays a recorded voice into VB-CABLE's "CABLE Input". Windows hands the same sound to "CABLE Output", the only
/// microphone on the test machine, so Coworker hears it exactly as it would hear a person.
/// </summary>
internal static class Speaker
{
    public const string CableInput = "CABLE Input";

    public static IReadOnlyList<string> PlaybackDevices() =>
        Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName).ToList();

    /// Plays the clip to the end and returns how long it was.
    public static TimeSpan Play(string wavPath)
    {
        var device = CableDevice();
        if (device < 0)
            throw new InvalidOperationException(
                $"No '{CableInput}' playback device (is VB-CABLE installed?). Devices: {string.Join(", ", PlaybackDevices())}");

        using var reader = new WaveFileReader(wavPath);
        using var output = new WaveOutEvent { DeviceNumber = device, DesiredLatency = 100 };
        output.Init(reader);
        output.Play();
        var deadline = DateTime.UtcNow + reader.TotalTime + TimeSpan.FromSeconds(5);
        while (output.PlaybackState == PlaybackState.Playing && DateTime.UtcNow < deadline) Thread.Sleep(20);
        return reader.TotalTime;
    }

    /// The cable's playback side. Windows may name it "CABLE Input (VB-Audio Virtual Cable)" or, with no rename,
    /// "Speakers (VB-Audio Virtual Cable)"; device names are cut to 31 characters. The 16-channel variant
    /// ("CABLE In 16 Ch") is a different cable and is skipped.
    private static int CableDevice()
    {
        var names = PlaybackDevices();
        for (var i = 0; i < names.Count; i++)
            if (names[i].Contains(CableInput, StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < names.Count; i++)
            if (names[i].Contains("VB-Audio Virtual Cab", StringComparison.OrdinalIgnoreCase) &&
                !names[i].Contains("16 Ch", StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// Saves 16 kHz mono 16-bit PCM (what Coworker streams) as a WAV file, to listen to what the app recorded.
    public static void SavePcm16k(byte[] pcm, string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1));
        writer.Write(pcm, 0, pcm.Length);
    }

    /// Loudness of 16-bit PCM, 0 (silence) to 1 (full scale).
    public static double Rms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0;
        var samples = pcm.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var s = BitConverter.ToInt16(pcm, i * 2) / 32768.0;
            sum += s * s;
        }
        return Math.Sqrt(sum / samples);
    }
}
