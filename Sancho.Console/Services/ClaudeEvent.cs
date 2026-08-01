namespace Sancho.Console.Services;

/// <summary>
/// Events emitted by <see cref="ClaudeService.RunAsync"/>.
/// The orchestrator consumes these and renders them to the display.
/// </summary>
public abstract record ClaudeEvent
{
    /// <summary>Service is ready to accept a sentence via <see cref="ClaudeService.Send"/>.</summary>
    public sealed record Ready : ClaudeEvent
    {
        public static readonly Ready Instance = new();

        private Ready()
        {
        }
    }

    /// <summary>A chunk of streaming assistant text.</summary>
    public sealed record AssistantText(string Text) : ClaudeEvent;

    /// <summary>Claude started a tool invocation.</summary>
    public sealed record ToolUse(string Name, string InputPreview) : ClaudeEvent;

    /// <summary>A tool execution completed.</summary>
    public sealed record ToolResult(string ToolId, bool IsError) : ClaudeEvent;

    /// <summary>The current response turn is complete.</summary>
    public sealed record TurnComplete : ClaudeEvent
    {
        public static readonly TurnComplete Instance = new();

        private TurnComplete()
        {
        }
    }

    /// <summary>A turn has started — the service received input and is processing.</summary>
    public sealed record TurnStart : ClaudeEvent
    {
        public static readonly TurnStart Instance = new();

        private TurnStart()
        {
        }
    }

    /// <summary>Status / stderr / non-JSON stdout message.</summary>
    public sealed record Status(string Message, ClaudeStatusKind Kind) : ClaudeEvent;

    /// <summary>Process-level failure (crash, connection lost).</summary>
    public sealed record Error(string Message) : ClaudeEvent;
}

public enum ClaudeStatusKind
{
    Info,
    Stderr,
    Warning
}