using System.Threading.Channels;

namespace Sancho.Console.Audio;

/// <summary>
/// A source of audio that writes 16-bit PCM data to a channel.
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// Begin capturing audio and writing it to <paramref name="writer"/>.
    /// Each written <see cref="byte"/>[] is a chunk of 16-bit PCM data.
    /// </summary>
    /// <param name="writer">The channel to write audio chunks into.</param>
    /// <param name="cancellationToken">Cancel to stop capture.</param>
    /// <returns>A task that completes when capture ends (via cancellation, error, or the source stopping naturally).</returns>
    /// <remarks>The writer is completed when capture ends, signalling downstream consumers to finalize.</remarks>
    Task CaptureAsync(ChannelWriter<byte[]> writer, CancellationToken cancellationToken = default);
}
