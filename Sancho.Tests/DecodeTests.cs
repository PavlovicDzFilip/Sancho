using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sancho.Console.Config;
using Sancho.Console.Transcription;
using Xunit;

namespace Sancho.Tests;

/// <summary>
/// End-to-end decode tests: a recorded fixture is fed through the real
/// silero VAD + whisper pipeline and the transcript is asserted. Whisper's
/// greedy decode is deterministic for identical audio and the pinned model,
/// so the expected phrase can be matched tightly (case-insensitive).
/// </summary>
public class DecodeTests
{
    /// <summary>The sentence the fixture must contain — record it with a little silence around it.</summary>
    private const string ExpectedPhrase = "the quick brown fox jumps over the lazy dog";

    private static readonly string[] AudioExtensions = [".wav", ".m4a", ".mp3", ".aac", ".flac"];

    [Fact]
    public async Task FixtureSentence_TranscribesKnownPhrase()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Fixture conversion uses NAudio's Media Foundation reader (Windows).");

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var fixture = Directory.Exists(fixturesDir)
            ? Directory.GetFiles(fixturesDir)
                .FirstOrDefault(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;
        if (fixture is null)
            Assert.Skip("No audio fixture present — record the expected phrase into Sancho.Tests/fixtures/.");

        // Pinned to small.en: the expected phrase below is only valid for
        // that model's greedy decode, regardless of the user's --model choice.
        var modelDir = Path.Combine(SanchoPaths.ModelsDir, WhisperModels.Small.ModelDirName);
        if (!Directory.Exists(modelDir))
            Assert.Skip("Whisper model not downloaded yet — run sancho once (downloads small.en).");

        var pcm = LoadAs24kMonoPcm16(fixture);
        var service = new LocalTranscriptionService(
            NullLogger<LocalTranscriptionService>.Instance,
            new LocalSttModels(NullLogger<LocalSttModels>.Instance, WhisperModels.Small));

        var completed = new List<string>();
        await foreach (var evt in service.TranscribeAsync(
            Chunked(pcm, ChunkSamples: 2400), TestContext.Current.CancellationToken))
        {
            if (evt is TranscriptionEvent.Completed c)
                completed.Add(c.Transcript);
        }

        Assert.NotEmpty(completed);
        var transcript = string.Join(" ", completed);
        Assert.Contains(ExpectedPhrase, transcript, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Feeds PCM16 bytes in 100 ms chunks, matching the capture cadence.</summary>
    private static async IAsyncEnumerable<byte[]> Chunked(byte[] pcm, int ChunkSamples)
    {
        for (var i = 0; i < pcm.Length; i += ChunkSamples * 2)
        {
            var count = Math.Min(ChunkSamples * 2, pcm.Length - i);
            yield return pcm.AsSpan(i, count).ToArray();
        }

        await Task.CompletedTask;
    }

    /// <summary>Converts any Media Foundation-readable audio file (wav, m4a, mp3, ...) to 24 kHz mono PCM16.</summary>
    private static byte[] LoadAs24kMonoPcm16(string path)
    {
        using var audio = new MediaFoundationReader(path);
        var channels = audio.WaveFormat.Channels;
        var resampler = new WdlResamplingSampleProvider(audio.ToSampleProvider(), 24000);

        var floats = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            floats.AddRange(buffer.AsSpan(0, read).ToArray());

        var mono = new float[floats.Count / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += floats[i * channels + c];
            mono[i] = sum / channels;
        }

        var pcm = new byte[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            var s = (short)(Math.Clamp(mono[i], -1f, 1f) * short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        return pcm;
    }
}
