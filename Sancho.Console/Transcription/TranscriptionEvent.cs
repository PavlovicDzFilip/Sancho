namespace Sancho.Console.Transcription;

/// <summary>
/// Events yielded by <see cref="ITranscriptionService"/>:
/// transcription output and connection status.
/// </summary>
public abstract record TranscriptionEvent
{
    /// <summary>A partial transcription delta — display inline as the user speaks.</summary>
    public sealed record Delta(string Text) : TranscriptionEvent;

    /// <summary>A finalized utterance — the sentence to forward to Claude.</summary>
    public sealed record Completed(string Transcript) : TranscriptionEvent;

    /// <summary>
    /// Speech detected in the microphone via silero VAD — drives the
    /// "hearing…" hint while the user is still talking.
    /// </summary>
    public sealed record SpeechDetected : TranscriptionEvent;

    /// <summary>
    /// An utterance ended and its whisper decode has started — drives the
    /// "transcribing…" hint until <see cref="Completed"/> arrives.
    /// </summary>
    public sealed record Decoding : TranscriptionEvent;

    /// <summary>The connection is up — transcription is live.</summary>
    public sealed record Connected : TranscriptionEvent;

    /// <summary>The connection dropped — a reconnect attempt is scheduled.</summary>
    public sealed record Reconnecting(string Message) : TranscriptionEvent;

    /// <summary>All reconnect attempts failed — transcription has stopped.</summary>
    public sealed record Failed(string Message) : TranscriptionEvent;

    private TranscriptionEvent() { } // sealed hierarchy
}
