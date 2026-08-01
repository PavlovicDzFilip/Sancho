namespace Sancho.Console.Transcription;

/// <summary>
/// Configuration for the OpenAI Realtime transcription service.
/// Bound from environment variables via <see cref="Microsoft.Extensions.Options.IOptions{T}"/>.
/// </summary>
public sealed class TranscriptionOptions
{
    /// <summary>
    /// OpenAI API key. Set via the <c>OPENAI_API_KEY</c> environment variable.
    /// </summary>
    public string ApiKey { get; set; } = "";
}
