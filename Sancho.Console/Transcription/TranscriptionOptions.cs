namespace Sancho.Console.Transcription;

/// <summary>
/// Configuration for transcription. Bound from the <c>Transcription</c>
/// section via <see cref="Microsoft.Extensions.Options.IOptions{T}"/>.
/// </summary>
public sealed class TranscriptionOptions
{
    /// <summary>
    /// OpenAI API key. Set via the <c>OPENAI_API_KEY</c> environment variable.
    /// </summary>
    public string ApiKey { get; set; } = "";
}
