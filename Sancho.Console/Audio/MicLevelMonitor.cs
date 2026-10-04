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

    // A loud mean level alone is not clipping. At least 1% of samples
    // must reach 99.9% of the PCM rail in every chunk for ten seconds.
    private const double RailSampleThreshold = 0.999;
    private const double ClippedSampleFraction = 0.01;
    // Capture normally updates every 100 ms. Missing chunks must not turn
    // one transient peak into a sustained clipping warning.
    private const long MaximumClipUpdateGapMilliseconds = 1_000;

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
    private readonly object _clipGate = new();
    private long _clippedSince = -1;
    private long _lastClippedUpdate = -1;
    private int _seenSignal;

    /// <summary>Allows tests to inject a fake clock; defaults to the system tick counter.</summary>
    public MicLevelMonitor(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>Clear stale signal and clipping when capture stops or changes devices.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _levelBits, 0);
        Interlocked.Exchange(ref _meanLevelBits, 0);
        Interlocked.Exchange(ref _seenSignal, 0);
        Interlocked.Exchange(ref _silentSince, _clock());
        lock (_clipGate)
        {
            _clippedSince = -1;
            _lastClippedUpdate = -1;
        }
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
    /// True after ten seconds of continuously observed near-rail samples.
    /// This identifies digital clipping, without diagnosing hardware causes.
    /// </summary>
    public bool IsClipped
    {
        get
        {
            lock (_clipGate)
            {
                var now = _clock();
                return _clippedSince >= 0 && now - _clippedSince > ClippedWarnDelay.TotalMilliseconds
                    && now - _lastClippedUpdate <= MaximumClipUpdateGapMilliseconds;
            }
        }
    }

    /// <summary>Feeds one 16-bit PCM mono chunk into the level tracker.</summary>
    public void Update(byte[] chunk)
    {
        var peak = 0d;
        var sum = 0d;
        var count = 0;
        var railSamples = 0;
        for (var i = 0; i + 1 < chunk.Length; i += 2)
        {
            // Cast through int: Math.Abs(short.MinValue) throws OverflowException,
            // and full-scale negative samples (-32768) do occur on digital mics.
            var sample = Math.Abs((int)(short)(chunk[i] | chunk[i + 1] << 8)) / 32768d;
            sum += sample;
            if (sample >= RailSampleThreshold) railSamples++;
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

        // Keep the streak start and latest sample time coherent for the UI.
        lock (_clipGate)
        {
            if (count > 0 && (double)railSamples / count >= ClippedSampleFraction)
            {
                var now = _clock();
                if (_clippedSince < 0 || now - _lastClippedUpdate > MaximumClipUpdateGapMilliseconds)
                    _clippedSince = now;
                _lastClippedUpdate = now;
            }
            else
            {
                _clippedSince = -1;
                _lastClippedUpdate = -1;
            }
        }
    }
}
