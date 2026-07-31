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
        "You are a live assistant listening to a brainstorm conversation. " +
        "You receive transcribed sentences in realtime as people speak.\n\n" +
        "CRITICAL — three-mode output protocol:\n\n" +
        "1. NOTHING TO ADD → reply with ONLY a single '…' character. Use this when the " +
        "conversation is flowing and you have nothing useful to contribute. No other text, no tools.\n\n" +
        "2. 💡 VOLUNTEER AN IDEA → start your response with '💡 ' followed by a ONE-SENTENCE " +
        "thought, suggestion, or observation. Keep it brief — one line only. This is your " +
        "'quiet voice' for contributing ideas without interrupting the conversation flow. " +
        "No tools in this mode.\n\n" +
        "3. DIRECTLY ADDRESSED → when someone explicitly asks you a question, gives you an " +
        "instruction, or says your name, respond normally. Answer the question, execute the " +
        "requested action, or ask a clarifying question. Keep responses concise.\n\n" +
        "Examples:\n" +
        "Speaker: 'so we need to build an API for the payments module'\n" +
        "You: …\n\n" +
        "Speaker: 'how would we handle idempotency'\n" +
        "You: 💡 consider using idempotency keys stored in Redis with a TTL\n\n" +
        "Speaker: 'claude, can you scaffold a new .NET project for this'\n" +
        "You: Sure. [uses dotnet new webapi]";
}
