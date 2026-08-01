using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Sancho.Console.Audio;

/// <summary>
/// Captures audio from a microphone device using NAudio and writes
/// it as 16-bit PCM chunks at 24000 Hz into a channel.
/// </summary>
public sealed class MicrophoneAudioSource : IAudioSource, IDisposable
{
    private readonly int _deviceNumber;
    private const int SampleRate = 24000;
    private readonly ILogger<MicrophoneAudioSource> _logger;
    private WaveInEvent? _waveIn;

    private MicrophoneAudioSource(int deviceNumber, ILogger<MicrophoneAudioSource> logger)
    {
        _deviceNumber = deviceNumber;
        _logger = logger;
    }

    /// <summary>
    /// Create a microphone audio source. Logs available devices and, when
    /// <paramref name="deviceNumber"/> is null and multiple devices exist,
    /// shows an interactive selector in the console.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="deviceNumber">
    /// Zero-based device index, or null to auto-pick (single device) or
    /// show an interactive selector (multiple devices).
    /// </param>
    /// <returns>A configured <see cref="MicrophoneAudioSource"/> ready for capture.</returns>
    public static MicrophoneAudioSource Create(
        ILogger<MicrophoneAudioSource> logger,
        int? deviceNumber = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var count = WaveInEvent.DeviceCount;

        if (count == 0)
        {
            logger.LogWarning("No recording devices found");
            throw new InvalidOperationException(
                "No recording devices found. Plug in a microphone and try again.");
        }

        // --- Resolve device: explicit arg, auto-pick, or interactive selector ---
        int selected;
        if (deviceNumber is not null)
        {
            selected = deviceNumber.Value;
            if (selected < 0 || selected >= count)
            {
                logger.LogError(
                    "Requested device [{Requested}] is out of range (available: 0–{Max})",
                    selected, count - 1);
                throw new ArgumentOutOfRangeException(
                    nameof(deviceNumber),
                    $"Device [{selected}] does not exist. Available devices: 0–{count - 1}.");
            }
        }
        else if (count == 1)
        {
            selected = 0;
        }
        else
        {
            selected = PromptDeviceSelection(count);
            System.Console.WriteLine(); // blank line after selector
        }

        var deviceName = WaveInEvent.GetCapabilities(selected).ProductName;
        logger.LogInformation("Using device [{Index}]: {Name}", selected, deviceName);

        return new MicrophoneAudioSource(selected, logger);
    }

    /// <summary>
    /// Interactive console selector. Draws a list navigable with arrow keys.
    /// </summary>
    private static int PromptDeviceSelection(int count)
    {
        var selected = 0;

        System.Console.WriteLine("🎤 Multiple microphones found. Use ↑/↓ to select, Enter to confirm:");
        var cursorTop = System.Console.CursorTop; // first device line will be here

        RenderDeviceList(cursorTop, count, selected);

        while (true)
        {
            var key = System.Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow when selected > 0:
                    selected--;
                    RenderDeviceList(cursorTop, count, selected);
                    break;

                case ConsoleKey.DownArrow when selected < count - 1:
                    selected++;
                    RenderDeviceList(cursorTop, count, selected);
                    break;

                case ConsoleKey.Enter:
                    // Move cursor to after the list and return
                    System.Console.SetCursorPosition(0, cursorTop + count);
                    return selected;
            }
        }
    }

    private static void RenderDeviceList(int top, int count, int selected)
    {
        for (var i = 0; i < count; i++)
        {
            System.Console.SetCursorPosition(0, top + i);

            var caps = WaveInEvent.GetCapabilities(i);
            var prefix = i == selected ? "  ▶" : "    ";
            var line = $"{prefix} [{i}] {caps.ProductName}";

            // Pad with spaces to clear any previous longer text
            System.Console.Write(line.PadRight(System.Console.WindowWidth - 1));
        }
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

        // NAudio fires DataAvailable on a background thread. The buffer
        // is reused between callbacks, so we copy before writing.
        _waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
                return;

            var chunk = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, chunk, e.BytesRecorded);

            // TryWrite returns false if the channel is full (bounded) or
            // completed — we skip the chunk in that case to avoid blocking
            // the capture thread.
            if (!writer.TryWrite(chunk))
                _logger.LogWarning("Dropped {Bytes} bytes — channel is full or completed", e.BytesRecorded);
        };

        _waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                _logger.LogError(e.Exception, "Recording stopped with error");
                tcs.TrySetException(e.Exception);
            }
            else
            {
                _logger.LogDebug("Recording stopped");
                tcs.TrySetResult();
            }
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
