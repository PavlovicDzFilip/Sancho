namespace Sancho.Console.Transcription;

/// <summary>
/// Configuration for transcription. Resolved in precedence order from the
/// <c>--api-key</c> flag, the <c>OPENAI_API_KEY</c> environment variable,
/// or <c>~/.sancho/config.json</c>.
/// </summary>
public sealed class TranscriptionOptions
{
    /// <summary>OpenAI API key.</summary>
    public string ApiKey { get; set; } = "";
}
