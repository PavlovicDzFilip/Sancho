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
}
