using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Sancho.Console.Transcription;

/// <summary>
/// Local speech-to-text via sherpa-onnx: silero VAD for utterance
/// segmentation + offline (non-streaming) zipformer English int8 for decode.
/// The offline encoder sees the whole utterance instead of a 16-chunk
/// streaming window, which is both more accurate and ~12x cheaper in CPU
/// (measured 0.16 cores vs 1.9 for the streaming model — see
/// <c>docs/feature/local-stt/ADR-0002-offline-zipformer-vad.md</c>).
/// Segmented utterances arrive as <see cref="TranscriptionEvent.Completed"/>
/// events at utterance end — there are no live deltas. No audio ever leaves
/// the machine.
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
    private const int VadSampleRate = 16000;

    /// <summary>How much pre-speech audio the pending buffer keeps while nothing has been said.</summary>
    private const int SilencePreRollSamples = CaptureSampleRate * 2;

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
            // Read without the token: the producer ends the stream by
            // completing the channel once it has handled cancellation itself.
            // (C# forbids yield return inside a try with a catch clause, so an
            // OCE must not originate on this side of the channel.)
            await foreach (var evt in events.Reader.ReadAllAsync())
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

            using var vad = CreateVad(modelDir);
            using var recognizer = CreateRecognizer(modelDir);

            events.TryWrite(new TranscriptionEvent.Connected());

            // Pending accumulates the 24 kHz audio since the last utterance, so
            // every decode gets its full pre-roll for free (silero's onset
            // detection lags speech by a window or two). Decoding pending — not
            // the VAD's own 16 kHz segment — also keeps sherpa's internal
            // high-quality resampler on the recognizer path.
            var pending = new List<float>();
            var vadBatch = new List<float>();
            var hadSpeech = false;

            await foreach (var chunk in audioInput.WithCancellation(ct))
            {
                var samples = ToSamples(chunk);
                pending.AddRange(samples);
                if (!hadSpeech && pending.Count > SilencePreRollSamples)
                    pending.RemoveRange(0, pending.Count - SilencePreRollSamples);

                vadBatch.AddRange(ResampleTo16k(samples));

                // Feed the VAD once per 500 ms instead of every chunk: each
                // silero Run() leaves ORT threads spinning for a while, so ten
                // calls per second cost ~0.5 idle cores and two cost ~0.1.
                if (vadBatch.Count >= VadSampleRate / 2)
                {
                    vad.AcceptWaveform(vadBatch.ToArray());
                    vadBatch.Clear();
                    if (vad.IsSpeechDetected())
                        hadSpeech = true;

                    DecodeReadySegments(vad, recognizer, pending, events, ref hadSpeech);
                }
            }

            // The channel completed without cancellation — force out the final
            // utterance so its text still reaches Claude on graceful shutdown.
            vad.Flush();
            DecodeReadySegments(vad, recognizer, pending, events, ref hadSpeech);
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

    /// <summary>
    /// Decodes every segment the VAD has finished. The segment itself only
    /// marks the boundary — the audio sent to the recognizer is the pending
    /// buffer, which starts before the segment's speech and ends on the
    /// trailing silence that closed it.
    /// </summary>
    private static void DecodeReadySegments(
        VoiceActivityDetector vad,
        OfflineRecognizer recognizer,
        List<float> pending,
        ChannelWriter<TranscriptionEvent> events,
        ref bool hadSpeech)
    {
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            vad.Pop();
            if (segment.Samples.Length < VadSampleRate / 4 || !hadSpeech)
                continue; // shorter than 250 ms of speech: not an utterance

            var transcript = Decode(recognizer, pending);
            pending.Clear();
            hadSpeech = false;

            var text = transcript.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                events.TryWrite(new TranscriptionEvent.Completed(text));
        }
    }

    /// <summary>Batch-decodes one utterance and returns its final transcript.</summary>
    private static string Decode(OfflineRecognizer recognizer, List<float> samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(CaptureSampleRate, samples.ToArray());
        recognizer.Decode(stream);
        return stream.Result.Text;
    }

    /// <summary>Builds the VAD for utterance segmentation.</summary>
    private static VoiceActivityDetector CreateVad(string modelDir)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = Path.Combine(modelDir, "silero_vad.onnx");
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.5f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = 20f;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = VadSampleRate; // silero's native rate; capture audio is resampled to it
        config.NumThreads = 1;             // silero is tiny — extra threads only spin
        return new VoiceActivityDetector(config, 120); // 2 min buffer
    }

    /// <summary>Builds the recognizer for the offline zipformer English int8 model.</summary>
    private static OfflineRecognizer CreateRecognizer(string modelDir)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000; // the model's rate; capture audio is resampled to it
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder =
            Path.Combine(modelDir, "encoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Decoder =
            Path.Combine(modelDir, "decoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Joiner =
            Path.Combine(modelDir, "joiner-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 2;
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    /// <summary>Converts one PCM16 mono chunk into normalized float samples.</summary>
    private static float[] ToSamples(byte[] chunk)
    {
        var samples = new float[chunk.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan(i * 2, 2)) / 32768f;
        return samples;
    }

    /// <summary>
    /// Resamples 24 kHz capture audio to the VAD's 16 kHz rate by linear
    /// interpolation (3:2). The VAD only needs the speech envelope, so the
    /// cheaper resampler is fine here — the recognizer path keeps sherpa's
    /// internal windowed-sinc resampler instead.
    /// </summary>
    private static float[] ResampleTo16k(float[] samples)
    {
        var count = samples.Length * 2 / 3;
        var resampled = new float[count];
        for (var i = 0; i < count; i++)
        {
            var position = i * 1.5f;
            var first = (int)position;
            var fraction = position - first;
            var second = Math.Min(first + 1, samples.Length - 1);
            resampled[i] = samples[first] * (1 - fraction) + samples[second] * fraction;
        }
        return resampled;
    }
}
