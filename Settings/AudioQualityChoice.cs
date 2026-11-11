using System.Globalization;

namespace Coworker.Settings;

/// One row of Settings > Audio Quality (Mac: AudioQualityPreset in Constants.swift). `Id` is the
/// persisted AppSettings.AudioQualityPreset value.
public sealed record AudioQualityChoice(string Id, string DisplayName, string Subtitle, int BytesPerSecond)
{
    /// The Mac shows a "Recommended" pill on this one.
    public bool IsRecommended => Id == All[0].Id;

    /// "~<size> / 17s", the size of a typical 17-second recording.
    public string SizeEstimate => $"~{FormatBytes(BytesPerSecond * 17)} / 17s";

    public static IReadOnlyList<AudioQualityChoice> All { get; } = new AudioQualityChoice[]
    {
        new("compact_16k", "Compact (16 kHz)", "Smallest files, ideal for transcription. ~85% smaller than Hardware.", 32_000),
        new("standard_24k", "Standard (24 kHz)", "Balanced size and fidelity. Good for ASR and lightweight TTS.", 48_000),
        new("high_44k", "High (44.1 kHz)", "CD-quality mono. Recommended if you plan to train a TTS model.", 88_200),
        new("hardware", "Hardware (native)", "Mic-native float32 WAV. Largest files — useful as a reference.", 352_800),
    };

    private static string FormatBytes(int bytes)
    {
        string[] units = { "B", "KB", "MB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var number = value >= 100 || unit == 0
            ? Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{number} {units[unit]}";
    }
}
