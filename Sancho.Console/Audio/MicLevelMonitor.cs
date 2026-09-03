namespace Sancho.Console.Audio;

/// <summary>
/// Tracks the live microphone signal level, fed by the capture sources once
/// per PCM chunk. Thread-safe: sources call <see cref="Update"/> from their
/// capture threads, the UI reads <see cref="Level"/>/<see cref="IsSilent"/>
/// from the render loop. Catches every cause of silence (muted switch, wrong
/// device, unplugged) regardless of platform.
/// </summary>
public sealed class MicLevelMonitor
{
    /// <summary>Peak below this counts as silence (about -48 dBFS).</summary>
    private const double SilenceThreshold = 0.004;

    /// <summary>Silence must persist this long before it is reported.</summary>
    private static readonly TimeSpan SilenceWarnDelay = TimeSpan.FromSeconds(3);

    /// <summary>Exponential decay per update (~0.65 s half-life at 10 chunks/s).</summary>
    private const double DecayPerUpdate = 0.85;

    private long _levelBits;
    private long _silentSince = -1;

    /// <summary>Recent signal level, 0..1 (peak, exponentially smoothed).</summary>
    public double Level =>
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _levelBits));

    /// <summary>True when the signal has been at/below the noise floor for a while.</summary>
    public bool IsSilent
    {
        get
        {
            var since = Interlocked.Read(ref _silentSince);
            return since >= 0 && Environment.TickCount64 - since > SilenceWarnDelay.TotalMilliseconds;
        }
    }

    /// <summary>Feeds one 16-bit PCM mono chunk into the level tracker.</summary>
    public void Update(byte[] chunk)
    {
        var peak = 0d;
        for (var i = 0; i + 1 < chunk.Length; i += 2)
        {
            // Cast through int: Math.Abs(short.MinValue) throws OverflowException,
            // and full-scale negative samples (-32768) do occur on digital mics.
            var sample = Math.Abs((int)(short)(chunk[i] | chunk[i + 1] << 8)) / 32768d;
            if (sample > peak)
                peak = sample;
        }

        var level = Math.Max(peak, Level * DecayPerUpdate);
        Interlocked.Exchange(ref _levelBits, BitConverter.DoubleToInt64Bits(level));

        if (level < SilenceThreshold)
        {
            if (Interlocked.Read(ref _silentSince) < 0)
                Interlocked.Exchange(ref _silentSince, Environment.TickCount64);
        }
        else
        {
            Interlocked.Exchange(ref _silentSince, -1);
        }
    }
}
