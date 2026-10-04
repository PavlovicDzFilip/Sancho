using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Audio;

/// <summary>Keeps one PCM stream open while devices disconnect, reconnect, or change priority.</summary>
public sealed class ResilientAudioSource(
    Func<AudioSourceSelection?> select,
    ILogger<ResilientAudioSource> logger,
    TimeSpan? pollingInterval = null,
    Func<IReadOnlySet<string>, AudioSourceSelection?>? selectAvailable = null,
    TimeSpan? failedDeviceCooldown = null,
    Action? onDeviceStopped = null) : IAudioSource, IDisposable
{
    private readonly TimeSpan _pollInterval = pollingInterval ?? TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _disposed = new();
    private int _started;

    public async Task CaptureAsync(ChannelWriter<byte[]> writer, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Audio recovery source can only capture once.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var ct = lifetime.Token;
        string? announcedIdentity = null;
        var unavailable = false;
        var retryDelay = _pollInterval;
        var failedUntil = new Dictionary<string, long>(StringComparer.Ordinal);
        AudioSourceSelection? SelectAvailable()
        {
            var now = Environment.TickCount64;
            foreach (var key in failedUntil.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                failedUntil.Remove(key);
            var excluded = failedUntil.Keys.ToHashSet(StringComparer.Ordinal);
            var selection = selectAvailable is null ? select() : selectAvailable(excluded);
            return selection is not null && excluded.Contains(selection.Identity) ? null : selection;
        }
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                AudioSourceSelection? selection;
                try { selection = SelectAvailable(); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    selection = null;
                }
                if (selection is null)
                {
                    if (!unavailable) logger.LogWarning("Audio device unavailable — waiting for a recording device to reconnect.");
                    unavailable = true;
                    await Task.Delay(_pollInterval, ct);
                    continue;
                }

                if (selection.Identity != announcedIdentity || unavailable)
                    logger.LogInformation("Audio device: {Device}", selection.Description);
                announcedIdentity = selection.Identity;
                unavailable = false;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(30)
                {
                    FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
                });
                IAudioSource? source = null;
                Task? capture = null;
                var forward = ForwardAsync(channel.Reader, writer, attempt.Token);
                var switched = false;
                try
                {
                    source = selection.CreateSource();
                    capture = source.CaptureAsync(channel.Writer, attempt.Token);
                    while (!capture.IsCompleted)
                    {
                        if (await Task.WhenAny(capture, Task.Delay(_pollInterval, ct)) == capture) break;
                        ct.ThrowIfCancellationRequested();
                        retryDelay = _pollInterval; // A running capture recovered from previous startup failures.
                        AudioSourceSelection? current;
                        try { current = SelectAvailable(); }
                        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { current = null; }
                        if (current?.Identity != selection.Identity)
                        {
                            switched = true;
                            break;
                        }
                    }
                    if (!switched) await capture;
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (retryDelay == _pollInterval)
                        logger.LogWarning("Audio capture interrupted: {Message}. Retrying available devices.", ex.Message);
                }
                finally
                {
                    await attempt.CancelAsync();
                    if (capture is not null)
                    {
                        try { await capture; }
                        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
                        catch (Exception) { } // Reported by the source or the attempt above.
                    }
                    channel.Writer.TryComplete();
                    try { await forward; }
                    finally
                    {
                        (source as IDisposable)?.Dispose();
                        onDeviceStopped?.Invoke();
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (switched)
                {
                    retryDelay = _pollInterval;
                    continue;
                }
                // Failed opens and unexpected capture stops should try the next
                // preference, while permitting this device another chance later.
                failedUntil[selection.Identity] = Environment.TickCount64
                    + (long)(failedDeviceCooldown ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
                await Task.Delay(retryDelay, ct);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 30000));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { writer.TryComplete(); }
    }

    private static async Task ForwardAsync(ChannelReader<byte[]> source, ChannelWriter<byte[]> target, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in source.ReadAllAsync(ct)) target.TryWrite(chunk);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public void Dispose() => _disposed.Cancel();
}
