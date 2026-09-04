namespace Sancho.Console.Agents;

/// <summary>
/// A swappable agent backend (Claude CLI, Cursor, Codex, Hermes, ...). Every
/// backend speaks sancho's <see cref="AgentEvent"/> stream; the orchestrator
/// never knows which CLI produced the events.
/// </summary>
public abstract class AgentService
{
    /// <summary>Whether a previous session is being resumed on startup.</summary>
    public abstract bool ContinueSession { get; }

    /// <summary>
    /// Summary of a stored session, used by the <c>--continue</c> chooser.
    /// </summary>
    /// <param name="Title">User-facing session name, when one was saved;
    /// <c>null</c> to fall back to <see cref="Preview"/>.</param>
    public sealed record SessionSummary(string Id, DateTime LastActivity, string? Title, string Preview);

    /// <summary>Start the agent process and return a stream of events.</summary>
    public abstract IAsyncEnumerable<AgentEvent> RunAsync(CancellationToken ct = default);

    /// <summary>
    /// Send one transcribed sentence to the agent. Throws if the agent is not
    /// in a <see cref="AgentEvent.Ready"/> state.
    /// </summary>
    public abstract void Send(string sentence);

    /// <summary>Lists stored sessions for a target directory, newest first.</summary>
    public abstract IReadOnlyList<SessionSummary> ListSessions(string targetDirectory);

    /// <summary>
    /// Reads all user/assistant text messages from the resumed (or most
    /// recent) session, in chronological order.
    /// </summary>
    public abstract IReadOnlyList<(bool IsUser, string Text)> GetSessionMessages();

    /// <summary>
    /// Checks the agent CLI is installed and authenticated; throws
    /// <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    public abstract void VerifyAvailable();
}
