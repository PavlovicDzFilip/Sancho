namespace Sancho.Console.Transcription;

/// <summary>
/// Events yielded by <see cref="RealtimeTranscriptionService"/>.
/// Maps directly to OpenAI's realtime transcription event types.
/// </summary>
public abstract record TranscriptionEvent
{
    /// <summary>A partial transcription delta — display inline as the user speaks.</summary>
    public sealed record Delta(string Text) : TranscriptionEvent;

    /// <summary>A finalized utterance — the sentence to forward to Claude.</summary>
    public sealed record Completed(string Transcript) : TranscriptionEvent;

    /// <summary>A WebSocket or API error occurred.</summary>
    public sealed record Error(string Message) : TranscriptionEvent;

    private TranscriptionEvent() { } // sealed hierarchy
}
