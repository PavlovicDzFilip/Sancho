using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Audio;

/// <summary>
/// Captures microphone audio by running ffmpeg as a subprocess and reading
/// raw 16-bit PCM from its stdout. Works on all platforms: dshow on Windows,
/// avfoundation on macOS, pulse with an ALSA fallback on Linux. Requires
/// ffmpeg on PATH — the installer scripts install it. The channel writer is
/// completed when capture ends so downstream consumers can finalize.
/// </summary>
public sealed class FfmpegAudioSource : IAudioSource, IDisposable
{
    private const int SampleRate = 24000;
    private const int ChunkBytes = 4800; // 100 ms at 24 kHz, 16-bit mono
    private const int BackendStartupGraceMs = 2000;

    private readonly string _deviceDescription;
    private readonly string[] _inputArgs;
    private readonly string[]? _fallbackInputArgs;
    private readonly ILogger<FfmpegAudioSource> _logger;
    private readonly Display _display;
    private readonly MicLevelMonitor _micMonitor;

    private Process? _process;
    private Task? _stderrTask;

    public FfmpegAudioSource(
        string deviceDescription,
        string[] inputArgs,
        string[]? fallbackInputArgs,
        ILogger<FfmpegAudioSource> logger,
        Display display,
        MicLevelMonitor micMonitor)
    {
        _deviceDescription = deviceDescription;
        _inputArgs = inputArgs;
        _fallbackInputArgs = fallbackInputArgs;
        _logger = logger;
        _display = display;
        _micMonitor = micMonitor;
    }

    /// <summary>
    /// Pre-flight check used by Program.cs: ffmpeg must be on PATH.
    /// Throws <see cref="InvalidOperationException"/> with a friendly
    /// message when it is missing or broken.
    /// </summary>
    public static void VerifyFfmpegAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("ffmpeg could not be started.");

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("`ffmpeg -version` did not respond within 5 seconds.");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"`ffmpeg -version` exited with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "ffmpeg was not found. Install it (the installer script does this automatically) and try again.");
        }
    }

    /// <inheritdoc />
    public Task CaptureAsync(ChannelWriter<byte[]> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var tcs = new TaskCompletionSource();
        _ = Task.Run(() => CaptureLoopAsync(writer, tcs, cancellationToken));
        return tcs.Task;
    }

    private async Task CaptureLoopAsync(
        ChannelWriter<byte[]> writer, TaskCompletionSource tcs, CancellationToken ct)
    {
        try
        {
            _display.History.AppendLine($"🎤 {_deviceDescription}", Display.HistoryColor.Default);

            var captured = await TryCaptureAsync(_inputArgs, writer, ct).ConfigureAwait(false);
            if (!captured && _fallbackInputArgs is not null)
            {
                _logger.LogWarning("Default capture backend unavailable — falling back to ALSA");
                captured = await TryCaptureAsync(_fallbackInputArgs, writer, ct).ConfigureAwait(false);
            }

            if (!captured)
                _logger.LogError(
                    "ffmpeg capture failed. Check that ffmpeg is installed and the microphone is available.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Audio capture cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio capture failed: {Message}", ex.Message);
        }
        finally
        {
            KillProcess();
            if (_stderrTask is not null)
            {
                try { await _stderrTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            writer.TryComplete();
            tcs.TrySetResult();
        }
    }

    /// <summary>
    /// Runs one capture attempt against a backend. Returns true if audio was
    /// captured and the run ended naturally (cancellation or clean exit);
    /// false if ffmpeg failed to start or exited immediately, so the caller
    /// can fall back to the next backend.
    /// </summary>
    private async Task<bool> TryCaptureAsync(
        string[] inputArgs, ChannelWriter<byte[]> writer, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var arg in inputArgs)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(SampleRate.ToString());
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("pipe:1");

        var process = new Process { StartInfo = psi };
        _process = process;

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _logger.LogError(
                "ffmpeg was not found. Install it (the installer script does this automatically) and try again.");
            return false;
        }

        // ffmpeg prints diagnostics to stderr; surface them without blocking the pipe.
        _stderrTask = LogStderrAsync(process);

        var started = Stopwatch.StartNew();
        var buffer = new byte[ChunkBytes];
        var stream = process.StandardOutput.BaseStream;

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return true; // cancelled — the caller kills the process
            }

            if (read == 0)
                break; // ffmpeg closed stdout

            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            _micMonitor.Update(chunk);

            // TryWrite keeps the capture thread non-blocking, matching the
            // behaviour when the channel is full.
            if (!writer.TryWrite(chunk))
                _logger.LogWarning("Dropped {Bytes} bytes — channel is full or completed", read);
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await _stderrTask.ConfigureAwait(false);

        // A quick non-zero exit means the backend is unavailable (e.g. no pulse
        // server) — worth trying the fallback. A clean exit or a long session
        // counts as "we captured audio".
        return process.ExitCode == 0 || started.ElapsedMilliseconds >= BackendStartupGraceMs;
    }

    private async Task LogStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                _logger.LogWarning("ffmpeg: {Line}", line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // stream closed mid-read during shutdown
        }
    }

    /// <summary>
    /// Disposes the underlying ffmpeg process, if any.
    /// </summary>
    public void Dispose() => KillProcess();

    private void KillProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }

        process.Dispose();
    }
}
