using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Sancho.Console.Audio;

/// <summary>
/// Captures audio from a microphone device using NAudio (Windows only) and
/// writes it as 16-bit PCM chunks at 24000 Hz into a channel. The writer is
/// completed when capture ends, signalling downstream consumers to finalize.
/// </summary>
public sealed class MicrophoneAudioSource : IAudioSource, IDisposable
{
    private readonly int _deviceNumber;
    private readonly string _deviceName;
    private const int SampleRate = 24000;
    private readonly ILogger<MicrophoneAudioSource> _logger;
    private readonly Display _display;
    private readonly MicLevelMonitor _micMonitor;
    private WaveInEvent? _waveIn;

    public MicrophoneAudioSource(int deviceNumber, string deviceName,
        ILogger<MicrophoneAudioSource> logger, Display display, MicLevelMonitor micMonitor)
    {
        _deviceNumber = deviceNumber;
        _deviceName = deviceName;
        _logger = logger;
        _display = display;
        _micMonitor = micMonitor;
    }

    /// <inheritdoc />
    public Task CaptureAsync(ChannelWriter<byte[]> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var format = new WaveFormat(SampleRate, 16, 1); // 16-bit PCM, mono
        var tcs = new TaskCompletionSource();

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _deviceNumber,
            WaveFormat = format,
            BufferMilliseconds = 100 // balance between latency and event frequency
        };

        _logger.LogDebug("Starting capture: {SampleRate} Hz, {Bits}-bit, {Channels} ch, buffer {BufferMs} ms",
            format.SampleRate, format.BitsPerSample, format.Channels, _waveIn.BufferMilliseconds);

        _display.History.AppendLine($"🎤 Using device [{_deviceNumber}]: {_deviceName}", Display.HistoryColor.Default);

        // NAudio fires DataAvailable on a background thread. The buffer
        // is reused between callbacks, so we copy before writing.
        _waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
                return;

            var chunk = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, chunk, e.BytesRecorded);
            _micMonitor.Update(chunk);

            // TryWrite returns false if the channel is full (bounded) or
            // completed — we skip the chunk in that case to avoid blocking
            // the capture thread.
            if (!writer.TryWrite(chunk))
                _logger.LogWarning("Dropped {Bytes} bytes — channel is full or completed", e.BytesRecorded);
        };

        _waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                _logger.LogError(e.Exception, "Recording stopped with error");
            else
                _logger.LogDebug("Recording stopped");

            // Complete the channel so downstream consumers (e.g. the recorder)
            // finalize; the task itself reports errors only via the log.
            writer.TryComplete();
            tcs.TrySetResult();
        };

        // Stop recording when cancellation is requested
        cancellationToken.Register(() =>
        {
            _logger.LogDebug("Cancellation requested — stopping recording");
            try { _waveIn?.StopRecording(); }
            catch { /* may already be stopped */ }
        });

        _waveIn.StartRecording();
        return tcs.Task;
    }

    /// <summary>
    /// Disposes the underlying NAudio device handle.
    /// </summary>
    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveIn = null;
    }
}
