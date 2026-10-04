using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sancho.Console.Audio;
using Xunit;

namespace Sancho.Tests;

public class ResilientAudioSourceTests
{
    [Fact]
    public async Task SwitchingFallbackAndPreferredReconnectKeepsStreamAndNeverOverlapsCapture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tracker = new Tracker();
        var preferred = Selection("preferred", 1, tracker);
        var fallback = Selection("fallback", 2, tracker);
        AudioSourceSelection? selected = preferred;
        using var recovery = new ResilientAudioSource(() => Volatile.Read(ref selected),
            NullLogger<ResilientAudioSource>.Instance, TimeSpan.FromMilliseconds(10));
        var output = Channel.CreateUnbounded<byte[]>();
        var capture = recovery.CaptureAsync(output.Writer, timeout.Token);
        Assert.Equal("preferred", await tracker.Starts.Reader.ReadAsync(timeout.Token));
        Assert.Equal((byte)1, (await output.Reader.ReadAsync(timeout.Token))[0]);
        Volatile.Write(ref selected, fallback);
        Assert.Equal("fallback", await tracker.Starts.Reader.ReadAsync(timeout.Token));
        Assert.Equal((byte)2, (await output.Reader.ReadAsync(timeout.Token))[0]);
        Volatile.Write(ref selected, null);
        await WaitUntilAsync(() => Volatile.Read(ref tracker.Active) == 0, timeout.Token);
        Assert.False(capture.IsCompleted);
        Assert.False(output.Reader.Completion.IsCompleted);
        Volatile.Write(ref selected, preferred);
        Assert.Equal("preferred", await tracker.Starts.Reader.ReadAsync(timeout.Token));
        Assert.Equal((byte)1, (await output.Reader.ReadAsync(timeout.Token))[0]);
        recovery.Dispose();
        await capture.WaitAsync(timeout.Token);
        await output.Reader.Completion.WaitAsync(timeout.Token);
        Assert.Equal(0, tracker.Active);
        Assert.Equal(3, tracker.Disposed);
        Assert.Equal(1, tracker.MaximumActive);
    }

    [Fact]
    public async Task CaptureFailureRetriesWithoutClosingOuterStream()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tracker = new Tracker();
        var attempts = 0;
        var selection = new AudioSourceSelection("preferred", "device", () =>
            new FakeSource("same", 3, tracker, fail: Interlocked.Increment(ref attempts) == 1));
        var fallback = new AudioSourceSelection("fallback", "fallback", () =>
        {
            Interlocked.Increment(ref attempts);
            return new FakeSource("fallback", 3, tracker);
        });
        using var recovery = new ResilientAudioSource(() => selection,
            NullLogger<ResilientAudioSource>.Instance, TimeSpan.FromMilliseconds(10),
            excluded => excluded.Contains("preferred") ? fallback : selection);
        var output = Channel.CreateUnbounded<byte[]>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var capture = recovery.CaptureAsync(output.Writer, lifetime.Token);
        Assert.Equal((byte)3, (await output.Reader.ReadAsync(timeout.Token))[0]);
        Assert.Equal(2, attempts);
        lifetime.Cancel();
        await capture.WaitAsync(timeout.Token);
        Assert.Equal(0, tracker.Active);
        Assert.Equal(2, tracker.Disposed);
    }

    [Fact]
    public async Task FailedPreferredUsesFallbackThenRetriesPreferredAfterCooldown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tracker = new Tracker();
        var preferredAttempts = 0;
        var preferred = new AudioSourceSelection("preferred", "preferred", () =>
            new FakeSource("preferred", 1, tracker, fail: Interlocked.Increment(ref preferredAttempts) == 1));
        var fallback = Selection("fallback", 2, tracker);
        using var recovery = new ResilientAudioSource(() => preferred,
            NullLogger<ResilientAudioSource>.Instance, TimeSpan.FromMilliseconds(10),
            excluded => excluded.Contains("preferred") ? fallback : preferred,
            failedDeviceCooldown: TimeSpan.FromMilliseconds(100));
        var output = Channel.CreateUnbounded<byte[]>();
        var capture = recovery.CaptureAsync(output.Writer, timeout.Token);
        Assert.Equal("preferred", await tracker.Starts.Reader.ReadAsync(timeout.Token));
        Assert.Equal("fallback", await tracker.Starts.Reader.ReadAsync(timeout.Token));
        Assert.Equal((byte)2, (await output.Reader.ReadAsync(timeout.Token))[0]);
        Assert.Equal("preferred", await tracker.Starts.Reader.ReadAsync(timeout.Token));
        Assert.Equal((byte)1, (await output.Reader.ReadAsync(timeout.Token))[0]);
        recovery.Dispose();
        await capture.WaitAsync(timeout.Token);
        Assert.Equal(2, preferredAttempts);
        Assert.Equal(1, tracker.MaximumActive);
        Assert.Equal(3, tracker.Disposed);
    }

    [Fact]
    public void DeviceStopClearsStaleSignalState()
    {
        var monitor = new MicLevelMonitor();
        monitor.Update([255, 127, 255, 127]);
        Assert.True(monitor.HasSeenSignal);
        Assert.True(monitor.Level > 0);
        monitor.Reset();
        Assert.False(monitor.HasSeenSignal);
        Assert.Equal(0, monitor.Level);
        Assert.Equal(0, monitor.MeanLevel);
        Assert.False(monitor.IsClipped);
    }

    [Fact]
    public async Task CancellationWhileNoDeviceAvailableCompletesStream()
    {
        using var lifetime = new CancellationTokenSource();
        using var recovery = new ResilientAudioSource(() => null,
            NullLogger<ResilientAudioSource>.Instance, TimeSpan.FromMilliseconds(10));
        var output = Channel.CreateUnbounded<byte[]>();
        var capture = recovery.CaptureAsync(output.Writer, lifetime.Token);
        lifetime.Cancel();
        await capture.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await output.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    private static AudioSourceSelection Selection(string id, byte value, Tracker tracker) =>
        new(id, id, () => new FakeSource(id, value, tracker));

    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken ct)
    {
        while (!ready()) await Task.Delay(5, ct);
    }

    private sealed class Tracker
    {
        public readonly Channel<string> Starts = Channel.CreateUnbounded<string>();
        public int Active;
        public int MaximumActive;
        public int Disposed;
    }

    private sealed class FakeSource(string id, byte value, Tracker tracker, bool fail = false) : IAudioSource, IDisposable
    {
        public async Task CaptureAsync(ChannelWriter<byte[]> writer, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref tracker.Active);
            tracker.MaximumActive = Math.Max(tracker.MaximumActive, active);
            tracker.Starts.Writer.TryWrite(id);
            try
            {
                if (fail) throw new IOException("device disconnected");
                writer.TryWrite([value]);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref tracker.Active);
                writer.TryComplete();
            }
        }
        public void Dispose() => Interlocked.Increment(ref tracker.Disposed);
    }
}
