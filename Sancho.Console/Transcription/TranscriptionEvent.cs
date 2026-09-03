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

    /// <summary>
    /// Speech detected in the microphone — local mode via silero VAD, openai
    /// mode via the server's VAD turn detection. Drives the "hearing…"
    /// indicator while the user is still talking.
    /// </summary>
    public sealed record SpeechDetected : TranscriptionEvent;

    /// <summary>
    /// An utterance ended and its decode has started (local mode), or the
    /// server-VAD speech pause fired (openai mode) — drives the
    /// "transcribing…" indicator until <see cref="Completed"/> arrives.
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
