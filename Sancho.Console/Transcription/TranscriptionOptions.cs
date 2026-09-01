namespace Sancho.Console.Transcription;

/// <summary>
/// Configuration for transcription, resolved in Program.cs with precedence
/// flags &gt; env vars &gt; <c>~/.sancho/config.json</c> &gt; defaults.
/// </summary>
public sealed class TranscriptionOptions
{
    public const string OpenAi = "openai";
    public const string Record = "record";
    public const string Local = "local";

    /// <summary>OpenAI API key (needed in <c>openai</c> mode only).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Transcription backend: <see cref="OpenAi"/> (default), <see cref="Record"/>,
    /// or <see cref="Local"/> (on-device speech-to-text, no cloud).
    /// </summary>
    public string Mode { get; set; } = OpenAi;
}
