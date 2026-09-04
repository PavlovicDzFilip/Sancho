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

    /// <summary>Mean-abs level at/above this counts as clipped (~-14 dB mean).</summary>
    /// <remarks>
    /// Uses mean-abs rather than peak: a broken capture stage (e.g. the AMD ACP
    /// DMIC bug) fills most samples near full scale, while real speech averages
    /// far lower and decays during pauses.
    /// </remarks>
    private const double ClippedMeanThreshold = 0.2;

    /// <summary>Silence must persist this long before it is reported.</summary>
    private static readonly TimeSpan SilenceWarnDelay = TimeSpan.FromSeconds(3);

    /// <summary>Clipping must persist this long before it is reported.</summary>
    private static readonly TimeSpan ClippedWarnDelay = TimeSpan.FromSeconds(10);

    /// <summary>Exponential decay per update (~0.65 s half-life at 10 chunks/s).</summary>
    private const double DecayPerUpdate = 0.85;

    private readonly Func<long> _clock;
    private long _levelBits;
    private long _meanLevelBits;
    private long _silentSince = -1;
    private long _clippedSince = -1;
    private int _seenSignal;

    /// <summary>Allows tests to inject a fake clock; defaults to the system tick counter.</summary>
    public MicLevelMonitor(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>Recent signal level, 0..1 (peak, exponentially smoothed).</summary>
    public double Level =>
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _levelBits));

    /// <summary>
    /// True once any chunk has risen above the noise floor. The mute warning
    /// should only fire before this ever happens — silence between turns is
    /// normal, not a mute.
    /// </summary>
    public bool HasSeenSignal => Volatile.Read(ref _seenSignal) != 0;

    /// <summary>Recent mean-absolute level, 0..1 (exponentially smoothed).</summary>
    public double MeanLevel =>
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _meanLevelBits));

    /// <summary>True when the signal has been at/below the noise floor for a while.</summary>
    public bool IsSilent
    {
        get
        {
            var since = Interlocked.Read(ref _silentSince);
            return since >= 0 && _clock() - since > SilenceWarnDelay.TotalMilliseconds;
        }
    }

    /// <summary>
    /// True when the signal has been pinned near full scale for a while —
    /// idle room audio does not sit at 0 dB, so this means clipping or a
    /// broken capture stage (e.g. the known AMD ACP DMIC driver bug).
    /// </summary>
    public bool IsClipped
    {
        get
        {
            var since = Interlocked.Read(ref _clippedSince);
            return since >= 0 && _clock() - since > ClippedWarnDelay.TotalMilliseconds;
        }
    }

    /// <summary>Feeds one 16-bit PCM mono chunk into the level tracker.</summary>
    public void Update(byte[] chunk)
    {
        var peak = 0d;
        var sum = 0d;
        var count = 0;
        for (var i = 0; i + 1 < chunk.Length; i += 2)
        {
            // Cast through int: Math.Abs(short.MinValue) throws OverflowException,
            // and full-scale negative samples (-32768) do occur on digital mics.
            var sample = Math.Abs((int)(short)(chunk[i] | chunk[i + 1] << 8)) / 32768d;
            sum += sample;
            count++;
            if (sample > peak)
                peak = sample;
        }

        var level = Math.Max(peak, Level * DecayPerUpdate);
        Interlocked.Exchange(ref _levelBits, BitConverter.DoubleToInt64Bits(level));

        if (count > 0)
        {
            var meanLevel = Math.Max(sum / count, MeanLevel * DecayPerUpdate);
            Interlocked.Exchange(ref _meanLevelBits, BitConverter.DoubleToInt64Bits(meanLevel));
        }

        if (level < SilenceThreshold)
        {
            if (Interlocked.Read(ref _silentSince) < 0)
                Interlocked.Exchange(ref _silentSince, _clock());
        }
        else
        {
            Interlocked.Exchange(ref _silentSince, -1);
            Volatile.Write(ref _seenSignal, 1);
        }

        if (MeanLevel >= ClippedMeanThreshold)
        {
            if (Interlocked.Read(ref _clippedSince) < 0)
                Interlocked.Exchange(ref _clippedSince, _clock());
        }
        else
        {
            Interlocked.Exchange(ref _clippedSince, -1);
        }
    }
}
