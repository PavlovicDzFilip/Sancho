using Sancho.Console.Audio;
using Sancho.Console.Transcription;
using Xunit;

namespace Sancho.Tests;

public class LocalTranscriptionServiceMathTests
{
    [Fact]
    public void ToSamples_ConvertsPcm16ToNormalizedFloats()
    {
        // 32767, 0, -32768 as little-endian PCM16.
        var pcm = new byte[]
        {
            0xFF, 0x7F,
            0x00, 0x00,
            0x00, 0x80,
        };

        var samples = LocalTranscriptionService.ToSamples(pcm);

        Assert.Equal(3, samples.Length);
        Assert.Equal(32767 / 32768f, samples[0], 5);
        Assert.Equal(0f, samples[1]);
        Assert.Equal(-1f, samples[2], 5);
    }

    [Fact]
    public void ResampleTo16k_IsTwoThirdsTheLength()
    {
        var input = new float[3000];

        var output = LocalTranscriptionService.ResampleTo16k(input);

        Assert.Equal(2000, output.Length);
    }

    [Fact]
    public void ResampleTo16k_LinearlyInterpolates()
    {
        // Ramp 0..9: at position 1.5 the interpolated value is 1.5.
        var input = new float[10];
        for (var i = 0; i < 10; i++)
            input[i] = i;

        var output = LocalTranscriptionService.ResampleTo16k(input);

        Assert.Equal(1.5f, output[1], 4);
        Assert.Equal(3.0f, output[2], 4);
    }

    [Fact]
    public void ToMono_AveragesChannels()
    {
        // Two frames of stereo float32: (1,3) and (-1,-3).
        var buffer = new byte[16];
        BitConverter.GetBytes(1f).CopyTo(buffer, 0);
        BitConverter.GetBytes(3f).CopyTo(buffer, 4);
        BitConverter.GetBytes(-1f).CopyTo(buffer, 8);
        BitConverter.GetBytes(-3f).CopyTo(buffer, 12);

        var mono = LoopbackAudioSource.ToMono(buffer, 16, bytesPerSample: 4, channels: 2);

        Assert.Equal([2f, -2f], mono);
    }

    [Fact]
    public void JoinOverlappingParts_StripsTwoWordOverlap()
    {
        var joined = LocalTranscriptionService.JoinOverlappingParts(
            ["one two three", "two three four"]);

        Assert.Equal("one two three four", joined);
    }

    [Fact]
    public void JoinOverlappingParts_StripsThreeWordOverlap()
    {
        var joined = LocalTranscriptionService.JoinOverlappingParts(
            ["one two three four", "two three four five"]);

        Assert.Equal("one two three four five", joined);
    }

    [Fact]
    public void JoinOverlappingParts_OverlapLongerThanCap_StripsOnlyThree()
    {
        // The next window repeats words 2-5 of the previous one, but the
        // dedup cap is 3 words — and the last 3 previous words ("three four
        // five") don't match the first 3 of the next ("two three four"), so
        // nothing is stripped and the seam duplicates remain.
        var joined = LocalTranscriptionService.JoinOverlappingParts(
            ["one two three four five", "two three four five six"]);

        Assert.Equal("one two three four five two three four five six", joined);
    }

    [Fact]
    public void JoinOverlappingParts_NoOverlap_JoinsWithSpace()
    {
        var joined = LocalTranscriptionService.JoinOverlappingParts(
            ["hello world", "goodbye moon"]);

        Assert.Equal("hello world goodbye moon", joined);
    }

    [Fact]
    public void JoinOverlappingParts_SinglePart()
    {
        var joined = LocalTranscriptionService.JoinOverlappingParts(["solo"]);

        Assert.Equal("solo", joined);
    }

    [Fact]
    public void JoinOverlappingParts_CaseInsensitiveOverlap()
    {
        var joined = LocalTranscriptionService.JoinOverlappingParts(
            ["One TWO three", "two THREE four"]);

        Assert.Equal("One TWO three four", joined);
    }
}
