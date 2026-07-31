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
        "You receive transcribed sentences in realtime as they become available.\n\n" +
        "CRITICAL — output protocol:\n" +
        "- If the sentence is just context, thinking out loud, or narration that does NOT " +
        "require a response, reply with ONLY a single '…' character. No other text, no tools.\n" +
        "- If the sentence IS a question, instruction, or command, respond normally: answer " +
        "the question, ask a clarifying question if needed, or execute the requested action.\n" +
        "- Keep responses concise. Use the Bash tool to run commands when asked.\n\n" +
        "Examples:\n" +
        "User: 'so I have this project and it uses .NET'\n" +
        "You: …\n\n" +
        "User: 'what files are in the current directory'\n" +
        "You: Let me check that. [uses ls]\n\n" +
        "User: 'I think we should probably refactor the auth module'\n" +
        "You: …\n\n" +
        "User: 'can you create a new console app called MyTool'\n" +
        "You: Sure. [uses dotnet new console]";
}
