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
        for (var i = 0; i < 99; i++) { advance(100); m.Update(SignalChunk(short.MaxValue)); }
        advance(99);
        m.Update(SignalChunk(short.MaxValue));
        Assert.False(m.IsClipped);

        advance(2);
        m.Update(SignalChunk(short.MaxValue));
        Assert.True(m.IsClipped);
    }

    [Fact]
    public void LoudSignalWithoutRailSamples_DoesNotClip()
    {
        var (m, advance) = FakeClock();

        m.Update(SignalChunk(28_000)); // high mean and peak, but no rail samples
        for (var i = 0; i < 110; i++)
        {
            advance(100);
            m.Update(SignalChunk(28_000));
            Assert.False(m.IsClipped);
        }

        Assert.False(m.IsClipped);
        Assert.True(m.MeanLevel > 0.8);
    }

    [Fact]
    public void RailFractionMustReachOnePercentAndRecoverImmediately()
    {
        var (m, advance) = FakeClock();
        var chunk = SignalChunk(25_000);
        for (var i = 0; i < 24; i++) { chunk[i * 2] = 0; chunk[i * 2 + 1] = 128; } // -32768
        m.Update(chunk);
        for (var i = 0; i < 101; i++) { advance(100); m.Update(chunk); }
        Assert.True(m.IsClipped);
        chunk[0] = 0; chunk[1] = 0; // 23/2400 falls below 1%
        m.Update(chunk);
        Assert.False(m.IsClipped);
        Assert.True(m.MeanLevel > 0.7); // smoothed loudness cannot keep warning latched
    }

    [Fact]
    public void TransientRailsAndCaptureGapsCannotAccumulateTenSeconds()
    {
        var (m, advance) = FakeClock();
        m.Update(SignalChunk(short.MinValue));
        advance(11_000);
        Assert.False(m.IsClipped); // no more observed samples
        m.Update(SignalChunk(short.MinValue));
        Assert.False(m.IsClipped); // fresh streak after gap
        for (var i = 0; i < 50; i++) { advance(100); m.Update(SignalChunk(short.MinValue)); }
        m.Update(SilenceChunk());
        Assert.False(m.IsClipped);
        for (var i = 0; i < 50; i++) { advance(100); m.Update(SignalChunk(short.MinValue)); }
        Assert.False(m.IsClipped); // silence interrupted the previous streak
    }

    [Fact]
    public void EmptyOddAndNegativeRailChunksAreSafe()
    {
        var (m, advance) = FakeClock();
        m.Update([0, 128]); // short.MinValue must not overflow Math.Abs
        Assert.Equal(1, m.Level);
        advance(100);
        m.Update([]);
        m.Update([128]); // dangling byte is not a PCM sample
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
