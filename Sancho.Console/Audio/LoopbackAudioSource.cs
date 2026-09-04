using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Sancho.Console.Audio;

/// <summary>
/// Captures the system audio output (other meeting participants) via NAudio's
/// WASAPI loopback (Windows only) and writes it as 16-bit PCM mono chunks at
/// 24000 Hz — the same format the mic source produces, so the transcription
/// pipeline treats both streams identically.
/// </summary>
public sealed class LoopbackAudioSource : IAudioSource, IDisposable
{
    private const int TargetSampleRate = 24000;
    private const int ChunkSamples = TargetSampleRate / 10; // 100 ms chunks, same cadence as the mic

    private readonly ILogger<LoopbackAudioSource> _logger;
    private readonly Display _display;

    private WasapiLoopbackCapture? _capture;
    private Queue<float[]>? _queue;
    private WdlResamplingSampleProvider? _resampler;
    private readonly List<float> _pendingSamples = new();

    public LoopbackAudioSource(ILogger<LoopbackAudioSource> logger, Display display)
    {
        _logger = logger;
        _display = display;
    }

    /// <inheritdoc />
    public Task CaptureAsync(ChannelWriter<byte[]> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _capture = new WasapiLoopbackCapture();
        _queue = new Queue<float[]>();
        var mono = new QueuedMonoProvider(_queue, _capture.WaveFormat.SampleRate);
        _resampler = new WdlResamplingSampleProvider(mono, TargetSampleRate);

        var tcs = new TaskCompletionSource();

        // DataAvailable fires on one capture thread, so no locking is needed
        // around the queue and the pending buffer.
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
                return;

            _queue!.Enqueue(ToMono(e.Buffer, e.BytesRecorded,
                _capture.WaveFormat.BitsPerSample / 8, _capture.WaveFormat.Channels));
            DrainResampler(writer);
        };

        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                _logger.LogError(e.Exception, "Loopback recording stopped with error");
            else
                _logger.LogDebug("Loopback recording stopped");

            writer.TryComplete();
            tcs.TrySetResult();
        };

        cancellationToken.Register(() =>
        {
            _logger.LogDebug("Cancellation requested — stopping loopback recording");
            try { _capture?.StopRecording(); }
            catch { /* may already be stopped */ }
        });

        _display.History.AppendLine("🔊 Capturing system output (other meeting participants)");
        _logger.LogDebug("Starting loopback capture: {Rate} Hz, {Channels} ch — resampling to mono {Target} Hz",
            _capture.WaveFormat.SampleRate, _capture.WaveFormat.Channels, TargetSampleRate);

        _capture.StartRecording();
        return tcs.Task;
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
    }

    /// <summary>Converts one interleaved float capture buffer into mono samples.</summary>
    internal static float[] ToMono(byte[] buffer, int bytesRecorded, int bytesPerSample, int channels)
    {
        var frameCount = bytesRecorded / (bytesPerSample * channels);
        var mono = new float[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += BitConverter.ToSingle(buffer, (f * channels + c) * bytesPerSample);
            mono[f] = sum / channels;
        }

        return mono;
    }

    /// <summary>Pulls whatever the resampler has ready and emits whole 100 ms chunks.</summary>
    private void DrainResampler(ChannelWriter<byte[]> writer)
    {
        var scratch = new float[ChunkSamples];
        while (true)
        {
            var read = _resampler!.Read(scratch, 0, scratch.Length);
            if (read <= 0)
                break;
            _pendingSamples.AddRange(scratch.AsSpan(0, read).ToArray());
        }

        while (_pendingSamples.Count >= ChunkSamples)
        {
            var chunk = new byte[ChunkSamples * 2];
            for (var i = 0; i < ChunkSamples; i++)
            {
                var sample = (short)(Math.Clamp(_pendingSamples[i], -1f, 1f) * short.MaxValue);
                chunk[i * 2] = (byte)(sample & 0xFF);
                chunk[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            _pendingSamples.RemoveRange(0, ChunkSamples);
            if (!writer.TryWrite(chunk))
                _logger.LogWarning("Dropped loopback chunk — channel is full or completed");
        }
    }

    /// <summary>Feeds the resampler from a queue of mono float buffers.</summary>
    private sealed class QueuedMonoProvider(Queue<float[]> queue, int sourceRate) : ISampleProvider
    {
        private float[]? _current;
        private int _offset;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (_current is null || _offset >= _current.Length)
                {
                    if (queue.Count == 0)
                        break;
                    _current = queue.Dequeue();
                    _offset = 0;
                }

                var n = Math.Min(count - written, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset + written, n);
                _offset += n;
                written += n;
            }

            return written;
        }
    }
}
