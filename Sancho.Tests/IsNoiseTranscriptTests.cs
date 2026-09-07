using Sancho.Console.Transcription;
using Xunit;

namespace Sancho.Tests;

/// <summary>
/// Whisper hallucination filter: caption tags and punctuation-only output are
/// what the model returns for non-speech audio, and they must not reach the
/// agent as if the user had said them.
/// </summary>
public class IsNoiseTranscriptTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("[blank audio]")]
    [InlineData("[silence]")]
    [InlineData("[MUSIC]")]
    [InlineData("[BLANK_AUDIO] [SILENCE]")]
    [InlineData("[silence]  [music] ")]
    [InlineData("(static)")]
    [InlineData("( static )")]
    [InlineData("[STATIC]")]
    [InlineData("(static) (music)")]
    [InlineData("…")]
    [InlineData(".")]
    [InlineData("♪ ♫")]
    [InlineData(", -")]
    [InlineData("")]
    public void NoiseTranscripts_AreRejected(string text)
    {
        Assert.True(LocalTranscriptionService.IsNoiseTranscript(text));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    [InlineData("1 2 3")]
    [InlineData("hello [SILENCE]")] // real speech with a stray tag: keep, don't drop
    public void SpeechTranscripts_AreKept(string text)
    {
        Assert.False(LocalTranscriptionService.IsNoiseTranscript(text));
    }
}
