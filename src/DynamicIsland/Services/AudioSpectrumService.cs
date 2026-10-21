using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DynamicIsland.Platform;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland.Services;

/// <summary>
/// Loopback capture feeding the island's equalizer. Captures only the media app's process tree when it can be resolved from
/// the SMTC app id, otherwise the default output device. Capture runs only while there is demand; band levels are computed
/// on the caller's (UI) thread from the latest window of samples.
/// </summary>
public sealed class AudioSpectrumService : IDisposable
{
    public const int BandCount = 5;

    private const int FftOrder = 11;
    private const int FftSize = 1 << FftOrder;
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessRecheck = TimeSpan.FromSeconds(3);
    private const long SilenceAfterMs = 250;
    private static readonly double[] BandEdgesHz = [60, 160, 420, 1100, 2800, 9000];
    private const double DynamicRangeDb = 30;
    private const double ReferenceDecayDb = 0.2; // per read, ~6 dB/s at 30 fps
    private double _referenceDb = double.NegativeInfinity;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _ordered = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private int _ringPosition;
    private int _sampleRate = 48000;
    private long _lastSampleTick = long.MinValue;

    private readonly Action<float> _append;
    private CancellationTokenSource? _captureCts;
    private Task _captureTask = Task.CompletedTask;
    private bool _wanted;
    private string? _appId;
    private volatile bool _disposed;

    public AudioSpectrumService()
    {
        _append = Append;
        for (var i = 0; i < FftSize; i++)
            _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
    }

    /// <summary>Starts or stops capture. Call on the UI thread; cheap when nothing changes.</summary>
    public void SetDemand(bool wanted, string? appId)
    {
        if (_disposed || (wanted == _wanted && appId == _appId))
            return;        _wanted = wanted;
        _appId = appId;

        _captureCts?.Cancel();
        _captureCts = null;
        if (!wanted)
            return;

        var cts = new CancellationTokenSource();
        _captureCts = cts;
        var previous = _captureTask;
        _captureTask = Task.Run(() => RunAsync(appId, previous, cts.Token));
    }

    /// <summary>Fills levels in [0, 1]; returns false (all zero) when no recent audio arrived.</summary>
    public bool ReadBands(Span<float> levels)
    {
        int rate;
        lock (_gate)
        {
            if (Environment.TickCount64 - _lastSampleTick > SilenceAfterMs)
            {
                levels.Clear();
                return false;
            }
            var tail = FftSize - _ringPosition;
            Array.Copy(_ring, _ringPosition, _ordered, 0, tail);
            Array.Copy(_ring, 0, _ordered, tail, _ringPosition);
            rate = _sampleRate;
        }

        for (var i = 0; i < FftSize; i++)
            _fft[i] = new Complex { X = _ordered[i] * _window[i], Y = 0 };
        FastFourierTransform.FFT(true, FftOrder, _fft);

        var binHz = (double)rate / FftSize;
        var count = Math.Min(levels.Length, BandCount);
        Span<double> decibels = stackalloc double[BandCount];
        var loudest = double.NegativeInfinity;
        for (var band = 0; band < count; band++)
        {
            var low = Math.Max(1, (int)(BandEdgesHz[band] / binHz));
            var high = Math.Min(FftSize / 2 - 1, Math.Max(low, (int)(BandEdgesHz[band + 1] / binHz)));
            double peak = 0;
            for (var bin = low; bin <= high; bin++)
                peak = Math.Max(peak, _fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y);

            // Music energy falls with frequency; the per-band tilt keeps the upper bars from staying flat.
            decibels[band] = 10 * Math.Log10(peak + 1e-20) + band * 4.5;
            loudest = Math.Max(loudest, decibels[band]);
        }

        // Automatic gain: bars are measured against the loudest recent band, which follows a louder passage at once and
        // relaxes slowly in quiet ones. Absolute FFT scale differs between device and process loopback, so a fixed range
        // either saturates every bar or leaves them flat.
        _referenceDb = Math.Max(loudest, _referenceDb - ReferenceDecayDb);
        for (var band = 0; band < count; band++)
            levels[band] = (float)Math.Clamp((decibels[band] - (_referenceDb - DynamicRangeDb)) / DynamicRangeDb, 0, 1);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _captureCts?.Cancel();
    }

    private async Task RunAsync(string? appId, Task previous, CancellationToken token)
    {
        // Let the previous capture release the endpoint before opening a new one.
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await CaptureAsync(appId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Spectrum] capture failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CaptureAsync(string? appId, CancellationToken token)
    {
        var processId = AudioProcessResolver.Resolve(appId);
        using var enumerator = new MMDeviceEnumerator();
        using var device = processId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null;
        using var recorder = processId is { } pid
            ? await ActivateProcessLoopbackAsync(pid, token).ConfigureAwait(false)
            : new WasapiRecorderBuilder().WithDevice(device!).WithLoopbackCapture().Build();

        var format = recorder.WaveFormat;
        var decode = SampleDecoder.For(format)
            ?? throw new NotSupportedException($"Unsupported capture format {format}");
        lock (_gate)
        {
            _sampleRate = format.SampleRate;
            Array.Clear(_ring);
            _ringPosition = 0;
        }

        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureDataAvailableHandler onData = (buffer, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0)
                return;
            lock (_gate)
            {
                decode(buffer, format.Channels, _append);
                _lastSampleTick = Environment.TickCount64;
            }
        };
        EventHandler<StoppedEventArgs> onStopped = (_, args) => stopped.TrySetResult(args.Exception);
        recorder.DataAvailable += onData;
        recorder.RecordingStopped += onStopped;
        try
        {
            recorder.StartRecording();
            var lastCheck = Environment.TickCount64;
            while (!token.IsCancellationRequested)
            {
                if (stopped.Task.IsCompleted)
                    throw await stopped.Task.ConfigureAwait(false) ?? new IOException("Loopback capture stopped.");
                // The browser or player may restart; follow it instead of capturing a dead process.
                if (Environment.TickCount64 - lastCheck >= ProcessRecheck.TotalMilliseconds)
                {
                    lastCheck = Environment.TickCount64;
                    if (AudioProcessResolver.Resolve(appId) != processId)
                        return;
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        finally
        {
            recorder.DataAvailable -= onData;
            recorder.RecordingStopped -= onStopped;
            try
            {
                recorder.StopRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Spectrum] stop failed: {ex.Message}");
            }
        }
    }

    // Called under _gate from the capture thread.
    private void Append(float sample)
    {
        _ring[_ringPosition] = sample;
        _ringPosition = (_ringPosition + 1) % FftSize;
    }

    private static async Task<WasapiRecorder> ActivateProcessLoopbackAsync(uint processId, CancellationToken token)
    {
        var activation = new WasapiRecorderBuilder()
            .WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .BuildAsync();
        try
        {
            return await activation.WaitAsync(ActivationTimeout, token).ConfigureAwait(false);
        }
        catch
        {
            // WASAPI activation cannot be cancelled; dispose a late result instead of leaking the client.
            _ = activation.ContinueWith(task => task.Result.Dispose(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Mixes interleaved capture frames down to mono samples. Shared-mode loopback is almost always 32-bit float.</summary>
    private static class SampleDecoder
    {
        public delegate void Decode(ReadOnlySpan<byte> buffer, int channels, Action<float> sink);

        public static Decode? For(WaveFormat format) => format.BitsPerSample switch
        {
            32 when format.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible => DecodeFloat,
            16 when format.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.Extensible => DecodePcm16,
            _ => null
        };

        private static void DecodeFloat(ReadOnlySpan<byte> buffer, int channels, Action<float> sink)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (var frame = 0; frame + channels <= samples.Length; frame += channels)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                    sum += samples[frame + channel];
                sink(sum / channels);
            }
        }

        private static void DecodePcm16(ReadOnlySpan<byte> buffer, int channels, Action<float> sink)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            for (var frame = 0; frame + channels <= samples.Length; frame += channels)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                    sum += samples[frame + channel] / 32768f;
                sink(sum / channels);
            }
        }
    }
}

/// <summary>
/// Maps an SMTC app id to the root process of the app's audio session on the default output device, e.g. "Spotify.exe"
/// → Spotify, "Chrome" → the chrome.exe tree. Returns null when no session matches; the caller then captures the device.
/// </summary>
internal static class AudioProcessResolver
{
    private static readonly Dictionary<string, string> PackagedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SpotifyAB.SpotifyMusic"] = "Spotify",
        ["Microsoft.ZuneMusic"] = "Microsoft.Media.Player",
        ["AppleInc.AppleMusicWin"] = "AppleMusic"
    };

    public static uint? Resolve(string? appId)
    {
        var name = ProcessNameOf(appId);
        if (name is null)
            return null;

        try
        {
            var processes = ProcessTable.Snapshot();
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var sessions = device.AudioSessionManager.Sessions;

            uint? best = null;
            var bestPeak = -1f;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                var processId = session.GetProcessID;
                if (processId == 0 || processId == Environment.ProcessId ||
                    session.State == AudioSessionState.AudioSessionStateExpired ||
                    !processes.TryGetValue(processId, out var process) ||
                    !string.Equals(process.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var peak = session.AudioMeterInformation?.MasterPeakValue ?? 0;
                if (peak > bestPeak)
                {
                    bestPeak = peak;
                    best = ProcessTable.RootOfSameName(processId, processes);
                }
            }
            return best;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Spectrum] process resolve failed: {ex.Message}");
            return null;
        }
    }

    private static string? ProcessNameOf(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        var bang = appId.IndexOf('!');
        if (bang >= 0)
        {
            // Packaged app: "<Name>_<publisher hash>!<App>".
            var family = appId[..bang];
            var underscore = family.IndexOf('_');
            return PackagedApps.GetValueOrDefault(underscore >= 0 ? family[..underscore] : family);
        }

        var name = Path.GetFileName(appId);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
