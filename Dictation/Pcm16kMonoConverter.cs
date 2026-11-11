using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Coworker.Dictation;

/// <summary>
/// Turns what a capture device delivers (any PCM or float format, any channel count and rate) into the
/// 16 kHz signed 16-bit mono stream the transcription API wants. Push device buffers in, get 16 kHz
/// samples out through the callback. Not thread-safe: one capture thread feeds it.
/// </summary>
internal sealed class Pcm16kMonoConverter
{
    public const int TargetSampleRate = 16_000;

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly bool _needsResampling;
    private readonly PushSampleProvider? _source;
    private readonly WdlResamplingSampleProvider? _resampler;
    private float[] _mono = new float[4096];
    private readonly float[] _resampled = new float[4096];

    public Pcm16kMonoConverter(WaveFormat input)
    {
        _channels = input.Channels;
        _bytesPerSample = input.BitsPerSample / 8;
        _isFloat = input.Encoding == WaveFormatEncoding.IeeeFloat ||
                   (input is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);

        if (_channels <= 0 || _bytesPerSample is < 1 or > 4 || input.SampleRate <= 0)
            throw new ArgumentException("Unsupported capture format.");
        if (_isFloat && _bytesPerSample != 4)
            throw new ArgumentException("Unsupported float capture format.");

        _needsResampling = input.SampleRate != TargetSampleRate;
        if (_needsResampling)
        {
            _source = new PushSampleProvider(input.SampleRate);
            _resampler = new WdlResamplingSampleProvider(_source, TargetSampleRate);
        }
    }

    /// Converts <paramref name="count"/> bytes of device data; calls <paramref name="emit"/> with 16 kHz mono samples as they become available.
    public void Push(byte[] buffer, int count, Action<ReadOnlySpan<short>> emit)
    {
        var frameBytes = _channels * _bytesPerSample;
        var frames = count / frameBytes;
        if (frames == 0) return;
        if (_mono.Length < frames) _mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var offset = frame * frameBytes;
            for (var channel = 0; channel < _channels; channel++)
                sum += ReadSample(buffer, offset + channel * _bytesPerSample);
            _mono[frame] = sum / _channels;
        }

        if (!_needsResampling)
        {
            EmitFloat(_mono.AsSpan(0, frames), emit);
            return;
        }

        _source!.Add(_mono, frames);
        int produced;
        while ((produced = _resampler!.Read(_resampled, 0, _resampled.Length)) > 0)
            EmitFloat(_resampled.AsSpan(0, produced), emit);
    }

    private float ReadSample(byte[] buffer, int offset)
    {
        if (_isFloat) return BitConverter.ToSingle(buffer, offset);

        return _bytesPerSample switch
        {
            1 => (buffer[offset] - 128) / 128f,
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => ((buffer[offset] << 8 | buffer[offset + 1] << 16 | buffer[offset + 2] << 24) >> 8) / 8388608f,
            _ => BitConverter.ToInt32(buffer, offset) / 2147483648f,
        };
    }

    private static void EmitFloat(ReadOnlySpan<float> samples, Action<ReadOnlySpan<short>> emit)
    {
        Span<short> pcm = stackalloc short[1024];
        for (var start = 0; start < samples.Length; start += pcm.Length)
        {
            var chunk = samples.Slice(start, Math.Min(pcm.Length, samples.Length - start));
            for (var i = 0; i < chunk.Length; i++)
                pcm[i] = (short)Math.Clamp((int)MathF.Round(chunk[i] * 32768f), short.MinValue, short.MaxValue);
            emit(pcm[..chunk.Length]);
        }
    }

    /// Feeds the resampler: it pulls whatever has been pushed and reports fewer samples than asked when drained.
    private sealed class PushSampleProvider : ISampleProvider
    {
        private float[] _queue = new float[8192];
        private int _count;

        public PushSampleProvider(int sampleRate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public WaveFormat WaveFormat { get; }

        public void Add(float[] samples, int count)
        {
            if (_count + count > _queue.Length) Array.Resize(ref _queue, Math.Max(_queue.Length * 2, _count + count));
            Array.Copy(samples, 0, _queue, _count, count);
            _count += count;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var taken = Math.Min(count, _count);
            Array.Copy(_queue, 0, buffer, offset, taken);
            Array.Copy(_queue, taken, _queue, 0, _count - taken);
            _count -= taken;
            return taken;
        }
    }
}
