namespace Sancho.Console.Transcription;

/// <summary>
/// Configuration for transcription, resolved in Program.cs with precedence
/// flags &gt; env vars &gt; <c>~/.sancho/config.json</c> &gt; defaults.
/// </summary>
public sealed class TranscriptionOptions
{
    public const string OpenAi = "openai";
    public const string Record = "record";

    /// <summary>OpenAI API key (needed in <c>openai</c> mode only).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Transcription backend: <see cref="OpenAi"/> (default) or <see cref="Record"/>.</summary>
    public string Mode { get; set; } = OpenAi;
}
