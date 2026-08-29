namespace Sancho.Console.Transcription;

/// <summary>
/// Turns a stream of PCM16 mono audio chunks into transcription events.
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// Consumes <paramref name="audioInput"/> and yields transcription events
    /// as the user speaks. Ends when the input ends, the provider gives up,
    /// or <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<byte[]> audioInput,
        CancellationToken ct = default);
}
