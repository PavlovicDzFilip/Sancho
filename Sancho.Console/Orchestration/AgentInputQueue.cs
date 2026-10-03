namespace Sancho.Console.Orchestration;

/// <summary>Serializes speech batches against the backend's Ready handshake.</summary>
internal sealed class AgentInputQueue
{
    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private bool _ready;
    private bool _stopped;

    public string[] Snapshot() { lock (_gate) return _pending.ToArray(); }
    public void Add(string sentence) { lock (_gate) { if (!_stopped) _pending.Add(sentence); } }
    public void MarkReady() { lock (_gate) { if (!_stopped) _ready = true; } }
    public void Stop() { lock (_gate) { _stopped = true; _ready = false; } }

    public string? TrySend(Action<string> send)
    {
        lock (_gate)
        {
            if (!_ready || _stopped || _pending.Count == 0) return null;
            var batch = string.Join(Environment.NewLine, _pending);
            // Reserve before calling Send: a synchronous Ready callback must not
            // be overwritten, and rejected sends must leave the transcript intact.
            _ready = false;
            send(batch);
            _pending.Clear();
            return batch;
        }
    }
}
