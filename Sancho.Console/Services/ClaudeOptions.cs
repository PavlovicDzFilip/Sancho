namespace Sancho.Console.Services;

/// <summary>
/// Configuration for the Claude CLI integration.
/// Bound from the <c>Claude</c> config section.
/// </summary>
public sealed class ClaudeOptions
{
    /// <summary>
    /// Working directory for the Claude CLI process. Defaults to the current directory.
    /// </summary>
    public string TargetDirectory { get; set; } = "";

    /// <summary>
    /// Path to a markdown file containing the system prompt.
    /// Relative paths resolve against the application base directory.
    /// </summary>
    public string PromptFilePath { get; set; } = "prompt.md";

    /// <summary>
    /// Continue the most recent Claude conversation instead of starting fresh.
    /// Set via the <c>--continue</c> command-line flag.
    /// </summary>
    public bool ContinueSession { get; set; }

    /// <summary>
    /// Resume a specific session by id. Set via the <c>--resume &lt;id&gt;</c> flag.
    /// </summary>
    public string ResumeSessionId { get; set; } = "";
}
