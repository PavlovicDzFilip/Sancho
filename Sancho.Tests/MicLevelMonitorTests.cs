using Sancho.Console.Audio;
using Xunit;

namespace Sancho.Tests;

public class MicLevelMonitorTests
{
    private static byte[] SilenceChunk() => new byte[2400 * 2]; // 100 ms of zeros

    private static byte[] SignalChunk(short amplitude)
    {
        var chunk = new byte[2400 * 2];
        for (var i = 0; i < 2400; i++)
        {
            chunk[i * 2] = (byte)(amplitude & 0xFF);
            chunk[i * 2 + 1] = (byte)((amplitude >> 8) & 0xFF);
        }

        return chunk;
    }

    private static (MicLevelMonitor Monitor, Action<long> Advance) FakeClock()
    {
        long t = 0;
        var monitor = new MicLevelMonitor(() => t);
        return (monitor, delta => t += delta);
    }

    [Fact]
    public void Silence_NeedsThreeSecondsBeforeReported()
    {
        var (m, advance) = FakeClock();

        m.Update(SilenceChunk());
        advance(2_999);
        Assert.False(m.IsSilent);

        advance(2);
        Assert.True(m.IsSilent);
    }

    [Fact]
    public void BriefSignal_ResetsSilence()
    {
        var (m, advance) = FakeClock();

        m.Update(SilenceChunk());
        advance(2_000);
        m.Update(SignalChunk(short.MaxValue)); // speaks briefly
        advance(2_000);

        Assert.False(m.IsSilent);
    }

    [Fact]
    public void HasSeenSignal_LatchesForever()
    {
        var m = new MicLevelMonitor(() => 0);

        Assert.False(m.HasSeenSignal);
        m.Update(SignalChunk(short.MaxValue));
        Assert.True(m.HasSeenSignal);
        m.Update(SilenceChunk());
        Assert.True(m.HasSeenSignal);
    }

    [Fact]
    public void QuietRoomNoise_DoesNotCountAsSignal()
    {
        var (m, advance) = FakeClock();

        m.Update(SignalChunk(64)); // ~0.002 peak — below the -48 dB threshold
        advance(3_001);

        Assert.False(m.HasSeenSignal);
        Assert.True(m.IsSilent);
    }

    [Fact]
    public void Clipping_NeedsTenSecondsBeforeReported()
    {
        var (m, advance) = FakeClock();

        m.Update(SignalChunk(short.MaxValue));
        advance(9_999);
        Assert.False(m.IsClipped);

        advance(2);
        Assert.True(m.IsClipped);
    }

    [Fact]
    public void ModerateSpeech_DoesNotClip()
    {
        var (m, advance) = FakeClock();

        m.Update(SignalChunk(6_000)); // mean-abs 0.183 — below the 0.2 clip threshold
        advance(11_000);

        Assert.False(m.IsClipped);
    }

    [Fact]
    public void Level_DecaysAfterPeak()
    {
        var m = new MicLevelMonitor(() => 0);

        m.Update(SignalChunk(short.MaxValue));
        var peak = m.Level;
        m.Update(SilenceChunk());

        Assert.Equal(peak * 0.85, m.Level, 5);
    }
}
