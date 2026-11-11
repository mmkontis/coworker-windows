namespace Coworker.Dictation;

public enum AudioStartError
{
    /// The microphone could not be opened, or reported no usable format (Mac: "Mic unavailable").
    NoDevice,

    /// Every attempt to capture failed (Mac: "Could not access mic").
    OpenFailed,
}

/// A finished take: a 16 kHz signed 16-bit mono WAV.
public sealed record CapturedAudio(byte[] Wav, TimeSpan Duration)
{
    public const int SampleRate = 16_000;
    private const int HeaderBytes = 44;

    public static CapturedAudio FromPcm(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[HeaderBytes + pcm.Length];
        using (var stream = new MemoryStream(wav))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
        }
        pcm.CopyTo(wav.AsSpan(HeaderBytes));
        return new CapturedAudio(wav, TimeSpan.FromSeconds(pcm.Length / 2.0 / SampleRate));
    }
}

/// The microphone as the dictation controller sees it (Mac: the AVAudioEngine tap in ContentViewModel).
public interface IAudioCapture
{
    /// Input level 0..1 (RMS x 8), about 12 times a second, on the capture thread.
    event Action<float>? LevelChanged;

    /// 16 kHz int16 mono PCM chunks as they are captured, on the capture thread.
    event Action<ReadOnlyMemory<byte>>? Frame;

    /// True from a successful start until stop or abort.
    bool IsCapturing { get; }

    /// Opens the microphone. The device is only open between this and stop, so Windows' microphone
    /// indicator is only lit while a take runs.
    Task<AudioStartError?> StartAsync();

    /// Ends the take and returns what was captured, or null when nothing was capturing.
    Task<CapturedAudio?> StopAsync();

    /// Ends the take and discards the audio.
    void Abort();
}
