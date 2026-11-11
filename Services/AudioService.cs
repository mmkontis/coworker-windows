using System.Runtime.InteropServices;
using Coworker.Dictation;
using Coworker.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Coworker.Services;

/// <summary>
/// Microphone capture through WASAPI (Mac: the AVAudioEngine tap in ContentViewModel). Audio flows
/// only while a take runs; nothing listens in between. Setting a stream up is the slow part (about half
/// a second on a laptop mic array, and speech in that gap was lost), so one is prepared ahead of time
/// (initialized, not started: Windows does not count it as the microphone being in use) and kept
/// between takes; starting it then takes a few milliseconds. It is prepared again when the capture
/// devices change, and any trouble with it falls back to opening the device afresh. Whatever the device
/// delivers is converted to 16 kHz int16 mono, kept for the WAV upload and streamed out in ~85 ms
/// frames with a level reading (RMS x 8) per frame.
/// A Bluetooth default microphone is avoided: opening it switches the headset to its call profile and
/// wrecks playback quality, so the built-in microphone is opened directly instead (Windows lets an app
/// capture from any endpoint without changing the system default).
/// </summary>
public sealed class AudioService : IAudioCapture
{
    public static AudioService Shared { get; } = new();

    /// True when Windows lists at least one active capture device.
    public static bool IsMicrophoneAvailable
    {
        get
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count > 0;
        }
    }

    // 85 ms of 16 kHz audio, the length of the Mac's 4096-frame tap buffer at 48 kHz.
    private const int WindowSamples = 1360;
    private const float LevelSensitivity = 8f;

    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    private const uint MicrophoneFormFactor = 4;

    private readonly object _gate = new();
    private readonly short[] _window = new short[WindowSamples];
    private readonly List<(byte[] Frame, float Level)> _emitted = new();

    // NAudio initializes the stream inside StartRecording; calling its initializer early is what lets
    // a stream be ready without capturing. Null if a later NAudio renames it (then streams are simply
    // set up on first use and reused after).
    private static readonly System.Reflection.MethodInfo? InitializeCaptureDevice = typeof(WasapiCapture).GetMethod(
        "InitializeCaptureDevice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private readonly object _prepareGate = new();
    private Prepared? _prepared;          // a stream ready to start, not capturing
    private string? _captureDeviceId;     // the device of the stream capturing now
    private string _captureName = "";
    private volatile bool _captureFailed; // the device stopped the stream with an error: don't keep it
    private DeviceWatcher? _watcher;

    private sealed record Prepared(WasapiCapture Capture, string DeviceId, string Name);

    private Task<AudioStartError?>? _starting;
    private Task _teardown = Task.CompletedTask;
    private WasapiCapture? _capture;
    private Pcm16kMonoConverter? _converter;
    private MemoryStream? _pcm;
    private ManualResetEventSlim? _stopped;
    private int _windowFill;

    private AudioService() { }

    public event Action<float>? LevelChanged;
    public event Action<ReadOnlyMemory<byte>>? Frame;

    public bool IsCapturing => _capture != null || (_starting is { IsCompleted: false });

    /// Readies a stream for the next take in the background (app start; after takes it is kept anyway).
    public void Prepare()
    {
        if (_watcher == null)
        {
            try { _watcher = new DeviceWatcher(this); }
            catch (COMException ex) { DebugLog.Write($"Microphone change notifications unavailable: {ex.Message}"); }
        }
        _ = Task.Run(PrepareCore);
    }

    private void PrepareCore()
    {
        lock (_prepareGate)
        {
            if (_prepared != null || IsCapturing) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = Candidates(enumerator).FirstOrDefault();
                if (device == null) return;

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var capture = CreateCapture(device);
                InitializeCaptureDevice?.Invoke(capture, null);
                _prepared = new Prepared(capture, device.ID, SafeName(device));
                DebugLog.Write($"Microphone ready: '{_prepared.Name}' in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or NotSupportedException
                                           or System.Reflection.TargetInvocationException)
            {
                DebugLog.Write($"Could not prepare the microphone: {(ex as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? ex.Message}");
            }
        }
    }

    /// Drops the prepared stream (the default microphone changed, or a device came or went).
    private void Unprepare()
    {
        var old = TakePrepared();
        if (old == null) return;
        try { old.Capture.Dispose(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    private Prepared? TakePrepared()
    {
        lock (_prepareGate)
        {
            var prepared = _prepared;
            _prepared = null;
            return prepared;
        }
    }

    public Task<AudioStartError?> StartAsync()
    {
        var starting = Task.Run(StartCore);
        _starting = starting;
        return starting;
    }

    public async Task<CapturedAudio?> StopAsync()
    {
        var starting = _starting;
        if (starting == null) return null;
        _starting = null;
        if (await starting.ConfigureAwait(false) != null) return null;

        return await Task.Run(() => Teardown(keepAudio: true)).ConfigureAwait(false);
    }

    public void Abort()
    {
        var starting = _starting;
        if (starting == null) return;
        _starting = null;

        // The device may still be opening on a worker thread; close it there rather than block the caller.
        _teardown = Task.Run(async () =>
        {
            if (await starting.ConfigureAwait(false) == null) Teardown(keepAudio: false);
        });
    }

    private AudioStartError? StartCore()
    {
        try
        {
            _teardown.Wait();
        }
        catch (AggregateException)
        {
            // A failed teardown already left the device closed.
        }

        var sawDevice = false;
        using var enumerator = new MMDeviceEnumerator();

        // The prepared stream, if it is for the microphone that would be chosen now.
        if (TakePrepared() is { } prepared)
        {
            var wanted = Candidates(enumerator).FirstOrDefault()?.ID;
            if (wanted == prepared.DeviceId && TryBegin(prepared)) return null;
            try { prepared.Capture.Dispose(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        }

        foreach (var device in Candidates(enumerator))
        {
            try
            {
                if (Open(device)) return null;
                sawDevice = true;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                sawDevice = true;
                DebugLog.Write($"Microphone '{SafeName(device)}' could not be opened: {ex.Message}");
            }
        }
        return sawDevice ? AudioStartError.OpenFailed : AudioStartError.NoDevice;
    }

    private bool Open(MMDevice device)
    {
        var capture = CreateCapture(device);
        var format = capture.WaveFormat;
        if (format.SampleRate <= 0 || format.Channels <= 0)
        {
            capture.Dispose();
            return false;   // the device reports no usable format (Mac: "Mic unavailable")
        }

        Begin(capture, device.ID, SafeName(device));
        return true;
    }

    // Starts a prepared stream; false (nothing started) if the device went away meanwhile.
    private bool TryBegin(Prepared prepared)
    {
        try
        {
            Begin(prepared.Capture, prepared.DeviceId, prepared.Name);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            DebugLog.Write($"Prepared microphone stream could not start ({ex.Message}); opening afresh");
            return false;
        }
    }

    private void Begin(WasapiCapture capture, string deviceId, string name)
    {
        var format = capture.WaveFormat;
        lock (_gate)
        {
            _converter = new Pcm16kMonoConverter(format);
            _pcm = new MemoryStream();
            _windowFill = 0;
            _stopped = new ManualResetEventSlim(false);
        }

        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnRecordingStopped;
        _captureFailed = false;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            throw;
        }
        _capture = capture;
        _captureDeviceId = deviceId;
        _captureName = name;
        DebugLog.Write($"Recording from '{name}' ({format})");
    }

    private static WasapiCapture CreateCapture(MMDevice device)
    {
        try
        {
            // Event-driven capture delivers a buffer every ~10 ms, which keeps the level meter lively.
            return new WasapiCapture(device, true, 20);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return new WasapiCapture(device);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        List<(byte[] Frame, float Level)>? ready = null;
        lock (_gate)
        {
            if (_converter == null || _pcm == null) return;
            _converter.Push(e.Buffer, e.BytesRecorded, OnSamples);
            if (_emitted.Count > 0)
            {
                ready = new List<(byte[], float)>(_emitted);
                _emitted.Clear();
            }
        }

        if (ready == null) return;
        foreach (var (frame, level) in ready)
        {
            LevelChanged?.Invoke(level);
            Frame?.Invoke(frame);
        }
    }

    // Gate held by the caller.
    private void OnSamples(ReadOnlySpan<short> samples)
    {
        foreach (var sample in samples)
        {
            _window[_windowFill++] = sample;
            if (_windowFill == WindowSamples) FlushWindow();
        }
    }

    // Gate held by the caller.
    private void FlushWindow()
    {
        if (_windowFill == 0) return;

        var frame = new byte[_windowFill * 2];
        double sum = 0;
        for (var i = 0; i < _windowFill; i++)
        {
            frame[i * 2] = (byte)_window[i];
            frame[i * 2 + 1] = (byte)(_window[i] >> 8);
            var normalized = _window[i] / 32768.0;
            sum += normalized * normalized;
        }

        _pcm!.Write(frame, 0, frame.Length);
        _emitted.Add((frame, (float)Math.Clamp(Math.Sqrt(sum / _windowFill) * LevelSensitivity, 0, 1)));
        _windowFill = 0;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _captureFailed = true;
            DebugLog.Write($"Recording stopped by the device: {e.Exception.Message}");
        }
        _stopped?.Set();
    }

    private CapturedAudio? Teardown(bool keepAudio)
    {
        var capture = _capture;
        _capture = null;
        if (capture == null) return null;

        try { capture.StopRecording(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        _stopped?.Wait(1000);

        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnRecordingStopped;

        // A stream that stopped cleanly is ready to start again: keep it for the next take.
        var keep = !_captureFailed && _captureDeviceId != null;
        lock (_prepareGate)
        {
            if (keep && _prepared == null) _prepared = new Prepared(capture, _captureDeviceId!, _captureName);
            else keep = false;
        }
        if (!keep)
        {
            try { capture.Dispose(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        }
        _captureDeviceId = null;

        CapturedAudio? audio = null;
        List<(byte[] Frame, float Level)>? tail = null;
        lock (_gate)
        {
            FlushWindow();
            if (keepAudio && _pcm != null) audio = CapturedAudio.FromPcm(_pcm.GetBuffer().AsSpan(0, (int)_pcm.Length));
            tail = _emitted.Count > 0 ? new List<(byte[], float)>(_emitted) : null;
            _emitted.Clear();
            _converter = null;
            _pcm = null;
            _stopped?.Dispose();
            _stopped = null;
        }

        if (keepAudio && tail != null)
        {
            foreach (var (frame, _) in tail) Frame?.Invoke(frame);
        }
        return audio;
    }

    // ===== Device selection =====

    // The default microphone, or the built-in one straight away when the default is Bluetooth. The
    // built-in microphone is also the fallback when the default cannot be opened (Mac: the same).
    private static IEnumerable<MMDevice> Candidates(MMDeviceEnumerator enumerator)
    {
        var seen = new HashSet<string>();
        MMDevice? current = null;
        try { current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
        catch (COMException) { }

        if (current != null && IsBluetooth(current))
        {
            var builtIn = FindBuiltIn(enumerator);
            if (builtIn != null && seen.Add(builtIn.ID)) yield return builtIn;
        }

        if (current != null && seen.Add(current.ID)) yield return current;

        var fallback = FindBuiltIn(enumerator);
        if (fallback != null && seen.Add(fallback.ID)) yield return fallback;
    }

    private static MMDevice? FindBuiltIn(MMDeviceEnumerator enumerator)
    {
        MMDevice? fallback = null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (IsBluetooth(device)) continue;
            var bus = ReadString(device, EnumeratorName);
            if (bus.Equals("USB", StringComparison.OrdinalIgnoreCase)) continue;
            if (ReadUInt(device, FormFactor) == MicrophoneFormFactor) return device;
            fallback ??= device;
        }
        return fallback;
    }

    private static bool IsBluetooth(MMDevice device) =>
        ReadString(device, EnumeratorName).StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

    private static string ReadString(MMDevice device, PropertyKey key)
    {
        try { return device.Properties.Contains(key) ? device.Properties[key].Value as string ?? "" : ""; }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return ""; }
    }

    private static uint ReadUInt(MMDevice device, PropertyKey key)
    {
        try { return device.Properties.Contains(key) && device.Properties[key].Value is uint value ? value : 0; }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return 0; }
    }

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch (COMException) { return "?"; }
    }

    // A prepared stream belongs to one device: when the capture devices change it is dropped and
    // prepared again for whatever would be chosen now. Callbacks come on a system thread.
    private sealed class DeviceWatcher : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        private readonly AudioService _owner;
        private readonly MMDeviceEnumerator _enumerator = new();

        public DeviceWatcher(AudioService owner)
        {
            _owner = owner;
            _enumerator.RegisterEndpointNotificationCallback(this);
        }

        private void Changed()
        {
            _owner.Unprepare();
            _ = Task.Delay(500).ContinueWith(_ => _owner.PrepareCore(), TaskScheduler.Default);
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Console) Changed();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed();
        public void OnDeviceAdded(string pwstrDeviceId) => Changed();
        public void OnDeviceRemoved(string deviceId) => Changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
