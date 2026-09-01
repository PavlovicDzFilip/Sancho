namespace Sancho.Console.Transcription;

/// <summary>
/// Events yielded by <see cref="ITranscriptionService"/>:
/// transcription output, recording status, and connection status.
/// </summary>
public abstract record TranscriptionEvent
{
    /// <summary>Audio is being recorded to a local file — no transcription is running.</summary>
    public sealed record Recording(string FilePath) : TranscriptionEvent;

    /// <summary>A partial transcription delta — display inline as the user speaks.</summary>
    public sealed record Delta(string Text) : TranscriptionEvent;

    /// <summary>A finalized utterance — the sentence to forward to Claude.</summary>
    public sealed record Completed(string Transcript) : TranscriptionEvent;

    /// <summary>The connection is up — transcription is live.</summary>
    public sealed record Connected : TranscriptionEvent;

    /// <summary>The connection dropped — a reconnect attempt is scheduled.</summary>
    public sealed record Reconnecting(string Message) : TranscriptionEvent;

    /// <summary>All reconnect attempts failed — transcription has stopped.</summary>
    public sealed record Failed(string Message) : TranscriptionEvent;

    private TranscriptionEvent() { } // sealed hierarchy
}
