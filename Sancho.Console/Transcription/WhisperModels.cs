namespace Sancho.Console.Transcription;

/// <summary>
/// One sherpa-onnx whisper model size: its HuggingFace repo layout and the
/// directory its files download into under <c>~/.sancho/models/</c>.
/// </summary>
public sealed record WhisperModelSpec(
    string Size,
    string ModelDirName,
    string BaseUrl,
    string EncoderFile,
    string DecoderFile,
    string TokensFile)
{
    public string EncoderUrl => BaseUrl + EncoderFile;
    public string DecoderUrl => BaseUrl + DecoderFile;
    public string TokensUrl => BaseUrl + TokensFile;
}

/// <summary>
/// The whisper .en int8 sizes sancho supports. Every size is its own
/// csukuangfj HF repo with the same file layout, so the entries below are
/// the single source of truth for the whitelist, the default, and the URLs
/// <see cref="LocalSttModels"/> downloads. The shared silero VAD lives in
/// <see cref="LocalSttModels"/>, not here — it is size-independent.
/// </summary>
public static class WhisperModels
{
    /// <summary>Size used when neither the <c>model</c> config key nor <c>--model</c> sets one.</summary>
    public const string DefaultSize = "small";

    /// <summary>Sizes accepted by <c>--model</c> / the <c>model</c> config key, small to large.</summary>
    public static readonly string[] SupportedSizes = ["tiny", "base", "small", "medium"];

    public static readonly WhisperModelSpec Tiny = New("tiny");
    public static readonly WhisperModelSpec Base = New("base");
    public static readonly WhisperModelSpec Small = New("small");
    public static readonly WhisperModelSpec Medium = New("medium");

    private static WhisperModelSpec New(string size) => new(
        size,
        $"sherpa-onnx-whisper-{size}.en",
        $"https://huggingface.co/csukuangfj/sherpa-onnx-whisper-{size}.en/resolve/main/",
        $"{size}.en-encoder.int8.onnx",
        $"{size}.en-decoder.int8.onnx",
        $"{size}.en-tokens.txt");

    /// <summary>Resolves a size name case-insensitively, or <c>null</c> when unknown.</summary>
    public static WhisperModelSpec? TryGet(string size) => size.ToLowerInvariant() switch
    {
        "tiny" => Tiny,
        "base" => Base,
        "small" => Small,
        "medium" => Medium,
        _ => null,
    };
}
