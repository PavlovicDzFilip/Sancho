using Sancho.Console.Transcription;
using Xunit;

namespace Sancho.Tests;

public class WhisperModelsTests
{
    [Theory]
    [InlineData("tiny", "tiny.en-encoder.int8.onnx")]
    [InlineData("base", "base.en-encoder.int8.onnx")]
    [InlineData("small", "small.en-encoder.int8.onnx")]
    [InlineData("medium", "medium.en-encoder.int8.onnx")]
    public void TryGet_ResolvesEachSize(string size, string expectedEncoder)
    {
        var spec = WhisperModels.TryGet(size);

        Assert.NotNull(spec);
        Assert.Equal(size, spec.Size);
        Assert.Equal($"sherpa-onnx-whisper-{size}.en", spec.ModelDirName);
        Assert.Equal(expectedEncoder, spec.EncoderFile);
        Assert.Equal($"{size}.en-decoder.int8.onnx", spec.DecoderFile);
        Assert.Equal($"{size}.en-tokens.txt", spec.TokensFile);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        Assert.NotNull(WhisperModels.TryGet("SMALL"));
    }

    [Theory]
    [InlineData("large")]
    [InlineData("")]
    [InlineData("tiny.en")]
    public void TryGet_UnknownReturnsNull(string size)
    {
        Assert.Null(WhisperModels.TryGet(size));
    }

    [Fact]
    public void SmallSpec_MatchesPinnedLayout()
    {
        // The layout current sancho releases download; kept explicit so a
        // rename in the upstream repo is noticed here rather than at runtime.
        var spec = WhisperModels.Small;

        Assert.Equal("small.en-encoder.int8.onnx", spec.EncoderFile);
        Assert.Equal("small.en-decoder.int8.onnx", spec.DecoderFile);
        Assert.Equal("small.en-tokens.txt", spec.TokensFile);
        Assert.StartsWith("https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small.en/", spec.BaseUrl);
        Assert.EndsWith(".onnx", spec.EncoderUrl);
    }

    [Fact]
    public void DefaultSize_IsSupported()
    {
        Assert.NotNull(WhisperModels.TryGet(WhisperModels.DefaultSize));
    }
}
