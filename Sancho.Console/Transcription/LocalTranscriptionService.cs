using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Sancho.Console.Transcription;

/// <summary>
/// Local speech-to-text via sherpa-onnx (streaming zipformer, English, int8).
/// Consumes the same 24 kHz mono 16-bit PCM stream as the other backends —
/// the engine resamples to its 16 kHz model rate internally. Utterances are
/// segmented by the engine's own endpoint detection: each endpoint yields a
/// <see cref="TranscriptionEvent.Completed"/> and the stream is reset for the
/// next utterance, mirroring what OpenAI Realtime's VAD does in the cloud.
/// No audio ever leaves the machine.
/// </summary>
/// <remarks>
/// Events flow from the producer through an unbounded channel: the iterator
/// must yield live deltas, and C# forbids <c>yield return</c> inside a try
/// block with a catch clause, so all error handling lives in
/// <see cref="ProduceAsync"/> instead.
/// </remarks>
public sealed class LocalTranscriptionService(
    ILogger<LocalTranscriptionService> logger,
    LocalSttModels models) : ITranscriptionService
{
    private const int CaptureSampleRate = 24000;

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<byte[]> audioInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var events = Channel.CreateUnbounded<TranscriptionEvent>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        var producer = ProduceAsync(audioInput, events.Writer, ct);

        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            // Re-surface producer failures once the channel has drained —
            // the producer completes the channel in its own finally block.
            await producer;
        }
    }

    /// <summary>Runs the recognizer and writes its events into the channel.</summary>
    private async Task ProduceAsync(
        IAsyncEnumerable<byte[]> audioInput,
        ChannelWriter<TranscriptionEvent> events,
        CancellationToken ct)
    {
        try
        {
            // Model files are downloaded once on first use; failures become a
            // Failed event instead of an exception so the UI shows a clear message.
            var modelDir = await models.EnsureDownloadedAsync(ct);
            if (modelDir is null)
            {
                events.TryWrite(new TranscriptionEvent.Failed(
                    "Could not download the speech model — check your connection and restart Sancho."));
                return;
            }

            using var recognizer = CreateRecognizer(modelDir);
            using var stream = recognizer.CreateStream();
            var lastDelta = "";

            events.TryWrite(new TranscriptionEvent.Connected());

            await foreach (var chunk in audioInput.WithCancellation(ct))
            {
                stream.AcceptWaveform(CaptureSampleRate, ToSamples(chunk));
                DecodePending(recognizer, stream);

                if (recognizer.IsEndpoint(stream))
                {
                    var final = FinalizeUtterance(recognizer, stream);
                    if (TryCompleted(final, ref lastDelta, out var completed))
                        events.TryWrite(completed);
                    recognizer.Reset(stream);
                }
                else
                {
                    EmitDelta(recognizer, stream, ref lastDelta, out var delta);
                    if (delta is not null)
                        events.TryWrite(new TranscriptionEvent.Delta(delta));
                }
            }

            // The channel completed without cancellation — finalize the last
            // utterance so its text still reaches Claude on graceful shutdown.
            var finalText = FinalizeUtterance(recognizer, stream);
            if (TryCompleted(finalText, ref lastDelta, out var finalCompleted))
                events.TryWrite(finalCompleted);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — stop without waiting for the engine to settle.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                       or DllNotFoundException or BadImageFormatException)
        {
            logger.LogError("Local transcription stopped: {Message}", ex.Message);
            events.TryWrite(new TranscriptionEvent.Failed(
                $"Local transcription stopped: {ex.Message}"));
        }
        finally
        {
            events.TryComplete();
        }
    }

    /// <summary>Builds the recognizer for the streaming zipformer English int8 model.</summary>
    private static OnlineRecognizer CreateRecognizer(string modelDir)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000; // the model's rate; capture audio is resampled to it
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder =
            Path.Combine(modelDir, "encoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx");
        config.ModelConfig.Transducer.Decoder =
            Path.Combine(modelDir, "decoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx");
        config.ModelConfig.Transducer.Joiner =
            Path.Combine(modelDir, "joiner-epoch-99-avg-1-chunk-16-left-128.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 4;
        config.DecodingMethod = "greedy_search";

        // Engine-side endpointing: an utterance ends after ~1.2 s of trailing
        // silence (or 20 s of continuous speech) — no separate VAD model needed.
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4F;
        config.Rule2MinTrailingSilence = 1.2F;
        config.Rule3MinUtteranceLength = 20.0F;

        return new OnlineRecognizer(config);
    }

    /// <summary>Converts one PCM16 mono chunk into normalized float samples.</summary>
    private static float[] ToSamples(byte[] chunk)
    {
        var samples = new float[chunk.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan(i * 2, 2)) / 32768f;
        return samples;
    }

    /// <summary>Decodes every frame the engine has ready for the stream.</summary>
    private static void DecodePending(OnlineRecognizer recognizer, OnlineStream stream)
    {
        while (recognizer.IsReady(stream))
            recognizer.Decode(stream);
    }

    /// <summary>
    /// Emits the growing part of the partial transcript as a delta, if the
    /// partial only extended the previous one. When the engine revises
    /// earlier tokens mid-stream, nothing is emitted — the final
    /// <see cref="TranscriptionEvent.Completed"/> is authoritative anyway.
    /// </summary>
    private static void EmitDelta(
        OnlineRecognizer recognizer, OnlineStream stream, ref string lastDelta, out string? delta)
    {
        var partial = recognizer.GetResult(stream).Text;
        if (partial.Length > lastDelta.Length && partial.StartsWith(lastDelta, StringComparison.Ordinal))
        {
            delta = partial[lastDelta.Length..];
            lastDelta = partial;
        }
        else
        {
            delta = null;
        }
    }

    /// <summary>
    /// Ends the current utterance and returns its final transcript. The
    /// encoder keeps ~0.6 s of lookahead, so trailing silence is fed first —
    /// otherwise the last word or two of the utterance never gets decoded.
    /// </summary>
    private static string FinalizeUtterance(OnlineRecognizer recognizer, OnlineStream stream)
    {
        stream.AcceptWaveform(CaptureSampleRate, new float[CaptureSampleRate * 6 / 10]);
        stream.InputFinished();
        DecodePending(recognizer, stream);
        return recognizer.GetResult(stream).Text;
    }

    /// <summary>
    /// Builds the event for a finished utterance, keeping deltas in sync.
    /// An utterance with no speech yields no event at all — a silent pause
    /// is not an error and must not stop the capture.
    /// </summary>
    private static bool TryCompleted(string finalText, ref string lastDelta, out TranscriptionEvent completed)
    {
        lastDelta = "";
        var text = finalText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            completed = null!;
            return false;
        }

        completed = new TranscriptionEvent.Completed(text);
        return true;
    }
}
