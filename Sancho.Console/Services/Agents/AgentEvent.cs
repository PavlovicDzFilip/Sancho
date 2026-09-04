namespace Sancho.Console.Agents;

/// <summary>
/// Events emitted by <see cref="AgentService.RunAsync"/>.
/// The orchestrator consumes these and renders them to the display.
/// </summary>
public abstract record AgentEvent
{
    /// <summary>Service is ready to accept a sentence via <see cref="AgentService.Send"/>.</summary>
    public sealed record Ready : AgentEvent
    {
        public static readonly Ready Instance = new();

        private Ready()
        {
        }
    }

    /// <summary>A chunk of streaming assistant text.</summary>
    public sealed record AssistantText(string Text) : AgentEvent;

    /// <summary>The agent started a tool invocation.</summary>
    public sealed record ToolUse(string Name, string InputPreview) : AgentEvent;

    /// <summary>A tool execution completed.</summary>
    public sealed record ToolResult(string ToolId, bool IsError) : AgentEvent;

    /// <summary>The current response turn is complete.</summary>
    public sealed record TurnComplete : AgentEvent
    {
        public static readonly TurnComplete Instance = new();

        private TurnComplete()
        {
        }
    }

    /// <summary>A turn has started — the service received input and is processing.</summary>
    public sealed record TurnStart : AgentEvent
    {
        public static readonly TurnStart Instance = new();

        private TurnStart()
        {
        }
    }

    /// <summary>Status / stderr / non-JSON stdout message.</summary>
    public sealed record Status(string Message, AgentStatusKind Kind) : AgentEvent;

    /// <summary>Process-level failure (crash, connection lost).</summary>
    public sealed record Error(string Message) : AgentEvent;
}

public enum AgentStatusKind
{
    Info,
    Stderr
}