namespace Sancho.Console;

/// <summary>
/// Configuration for the Claude CLI integration.
/// Bound from the <c>Claude</c> config section via <see cref="Microsoft.Extensions.Options.IOptions{T}"/>.
/// </summary>
public sealed class ClaudeOptions
{
    /// <summary>
    /// Directory where the <c>claude</c> CLI process runs (its working directory).
    /// Defaults to the current directory.
    /// </summary>
    public string TargetDirectory { get; set; } = "";

    /// <summary>
    /// System prompt that sets Claude's persona for the live assistant session.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are a live assistant listening to someone speak. " +
        "You receive transcribed sentences in realtime as they become available. " +
        "Respond briefly when appropriate — don't respond to every sentence, " +
        "only when action is needed or a question is asked. " +
        "When asked to run a command, use the Bash tool to execute it " +
        "and report the results clearly. " +
        "Acknowledge that you heard the speaker, but keep responses short and helpful.";
}
