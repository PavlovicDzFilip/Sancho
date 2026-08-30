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
    /// System prompt file. Empty means "auto": <c>.sancho.md</c> in the target
    /// directory if present, else <c>prompt.md</c> next to the executable.
    /// Relative paths resolve against the executable's directory.
    /// </summary>
    public string PromptFilePath { get; set; } = "";
}
