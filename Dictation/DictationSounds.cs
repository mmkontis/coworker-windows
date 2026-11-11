using System.Media;
using System.Runtime.InteropServices;
using Coworker.Helpers;
using Coworker.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Coworker.Dictation;

/// <summary>
/// The start/stop earcons (Mac: DictationSounds.swift, its synthesized replica of the Lemon dictation
/// sounds): two plucked sine blips, 720 Hz then 1080 Hz for start and the reverse for stop, each with a
/// 2 ms attack and a 12 ms exponential decay. The Mac plays them at rate 1.5 and volume 0.35 with the
/// pitch kept, so the timing is compressed by 1.5 and the volume baked into the samples here.
/// Each sound plays through an output stream set up in advance (Windows' PlaySound took 240-390 ms to be
/// heard, a ready stream about 100): the start chime then lands as the bar finishes opening. The stream
/// follows the default output device, and PlaySound remains the fallback.
/// </summary>
public sealed class DictationSounds
{
    public static DictationSounds Shared { get; } = new();

    private const int SampleRate = 44_100;
    private const double Rate = 1.5;
    private const double Volume = 0.35;

    private readonly Earcon _start = new(Build((720, 0.045), (1080, 0.145)));
    private readonly Earcon _stop = new(Build((1080, 0.030), (720, 0.128)));

    private DictationSounds() { }

    /// Sets up both output streams in the background, so even the first chime is quick.
    public void Prepare()
    {
        _ = Task.Run(_start.Prepare);
        _ = Task.Run(_stop.Prepare);
    }

    /// Played as the take begins.
    public void PlayStart() => Play(_start);

    /// Played when the dictated words land in the field (not on release, not on cancel or error).
    public void PlayStop() => Play(_stop);

    // Off the UI thread: WASAPI wants a multithreaded COM apartment, and a device change rebuilds the stream.
    private static void Play(Earcon earcon)
    {
        if (!AppSettings.Instance.DictationSoundsEnabled) return;
        _ = Task.Run(earcon.Play);
    }

    private sealed class Earcon(byte[] wav)
    {
        private readonly object _gate = new();
        private readonly SoundPlayer _fallback = Load(wav);
        private WasapiOut? _output;
        private WaveFileReader? _reader;
        private string? _deviceId;

        public void Prepare()
        {
            lock (_gate)
            {
                try { EnsureOutput(); }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { Drop(); }
            }
        }

        public void Play()
        {
            lock (_gate)
            {
                try
                {
                    EnsureOutput();
                    _output!.Stop();
                    _reader!.Position = 0;
                    _output.Play();
                    return;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    DebugLog.Write($"Earcon stream failed ({ex.Message}); using PlaySound");
                    Drop();
                }
            }
            try { _fallback.Play(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                // No audio output device: the take goes on without the earcon.
            }
        }

        // A stream for the current default output; rebuilt when that changed (headphones plugged in).
        private void EnsureOutput()
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (_output != null && device.ID == _deviceId) return;

            Drop();
            _reader = new WaveFileReader(new MemoryStream(wav));
            _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
            _output.Init(_reader);
            _deviceId = device.ID;
        }

        private void Drop()
        {
            try { _output?.Dispose(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
            _reader?.Dispose();
            _output = null;
            _reader = null;
            _deviceId = null;
        }
    }

    private static SoundPlayer Load(byte[] wav)
    {
        var player = new SoundPlayer(new MemoryStream(wav));
        try { player.Load(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException) { }
        return player;
    }

    /// The 16-bit mono WAV of a sound made of (frequency in Hz, start in seconds) blips.
    public static byte[] Build(params (double Hz, double At)[] blips)
    {
        var samples = new double[(int)(0.255 / Rate * SampleRate)];
        foreach (var (hz, at) in blips)
        {
            var start = (int)(at / Rate * SampleRate);
            var length = (int)(0.06 / Rate * SampleRate);
            for (var i = 0; i < length && start + i < samples.Length; i++)
            {
                var t = i / (double)SampleRate;
                var attack = Math.Min(1, t / (0.002 / Rate));
                var decay = Math.Exp(-t / (0.012 / Rate));
                samples[start + i] += Math.Sin(2 * Math.PI * hz * t) * attack * decay * 0.45 * Volume;
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var byteCount = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + byteCount);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(byteCount);
        foreach (var sample in samples)
            writer.Write((short)(Math.Clamp(sample, -1, 1) * (short.MaxValue - 1)));
        return stream.ToArray();
    }
}
