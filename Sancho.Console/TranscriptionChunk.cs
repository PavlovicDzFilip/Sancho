namespace Sancho.Console;

/// <summary>
/// A chunk of transcription text yielded by <see cref="RealtimeTranscriptionService"/>.
/// </summary>
/// <param name="Text">The text to display.</param>
/// <param name="IsComplete">
/// <see langword="true"/> when this is a finalized utterance (the "sentence" that
/// should be forwarded to Claude); <see langword="false"/> for partial deltas
/// (live typing effect only).
/// </param>
public readonly record struct TranscriptionChunk(string Text, bool IsComplete);
