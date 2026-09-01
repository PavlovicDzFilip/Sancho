using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Sancho.Console.Config;

namespace Sancho.Console.Transcription;

/// <summary>
/// Interim transcription replacement: records the PCM stream (24 kHz mono
/// 16-bit) to a WAV file locally instead of sending it to a cloud provider.
/// Yields a single <see cref="TranscriptionEvent.Recording"/> event with the
/// file path and otherwise stays quiet — local speech-to-text will replace
/// this on the same <see cref="ITranscriptionService"/> seam.
/// </summary>
public sealed class RecordingTranscriptionService(
    ILogger<RecordingTranscriptionService> logger) : ITranscriptionService
{
    private const int SampleRate = 24000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<byte[]> audioInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Setup errors are collected into a local because C# does not allow
        // yielding from inside a catch block.
        var setupError = (string?)null;
        var filePath = "";
        FileStream? stream = null;
        try
        {
            filePath = NextRecordingPath();
            stream = new FileStream(
                filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            await WriteWavHeaderAsync(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            setupError = $"Could not start recording: {ex.Message}";
        }

        if (setupError is not null)
        {
            yield return new TranscriptionEvent.Failed(setupError);
            yield break;
        }

        using (var file = stream!)
        {
            yield return new TranscriptionEvent.Recording(filePath);

            long bytesWritten = 0;
            var cancelled = false;
            var inputEnded = true;
            try
            {
                await foreach (var chunk in audioInput.WithCancellation(ct))
                {
                    await file.WriteAsync(chunk, ct);
                    bytesWritten += chunk.Length;
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true; // Ctrl+C — finalize the header below
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                logger.LogError("Recording write failed: {Message}", ex.Message);
                inputEnded = false;
            }

            await PatchWavSizesAsync(file, bytesWritten);

            if (cancelled || ct.IsCancellationRequested)
                yield break;

            if (!inputEnded)
            {
                yield return new TranscriptionEvent.Failed(
                    $"Recording stopped — could not write to {filePath}. The partial file was kept.");
            }
            else
            {
                // The channel completed without cancellation — audio capture stopped.
                yield return new TranscriptionEvent.Failed(
                    "Recording ended — the audio capture stopped unexpectedly. The file was kept.");
            }
        }
    }

    /// <summary>Writes the 44-byte PCM header; the RIFF/data sizes are patched on close.</summary>
    private static async Task WriteWavHeaderAsync(Stream stream)
    {
        var header = new byte[44];
        "RIFF"u8.CopyTo(header.AsSpan(0, 4));
        "WAVE"u8.CopyTo(header.AsSpan(8, 4));
        "fmt "u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16); // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);  // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), SampleRate * Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), BitsPerSample);
        "data"u8.CopyTo(header.AsSpan(36, 4));
        await stream.WriteAsync(header);
    }

    /// <summary>Fills in the byte counts in the header once recording ends.</summary>
    private async Task PatchWavSizesAsync(FileStream stream, long dataBytes)
    {
        try
        {
            var buf = new byte[4];
            stream.Seek(4, SeekOrigin.Begin);
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)Math.Min(36 + dataBytes, uint.MaxValue));
            await stream.WriteAsync(buf);
            stream.Seek(40, SeekOrigin.Begin);
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)Math.Min(dataBytes, uint.MaxValue));
            await stream.WriteAsync(buf);
            await stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.LogWarning("Could not patch WAV sizes: {Message}", ex.Message);
        }
    }

    /// <summary>Next available <c>sancho-&lt;timestamp&gt;.wav</c> path, never overwriting.</summary>
    private static string NextRecordingPath()
    {
        Directory.CreateDirectory(SanchoPaths.RecordingsDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        var candidate = Path.Combine(SanchoPaths.RecordingsDir, $"sancho-{stamp}.wav");
        var suffix = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(SanchoPaths.RecordingsDir, $"sancho-{stamp}-{suffix++}.wav");
        return candidate;
    }
}
