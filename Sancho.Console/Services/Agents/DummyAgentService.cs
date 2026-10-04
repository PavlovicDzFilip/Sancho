using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Sancho.Console.Agents;

/// <summary>Local transcription check: echo recognized speech without a CLI or account.</summary>
public sealed class DummyAgentService : AgentService
{
    private readonly object _gate = new();
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
    private bool _started;
    private bool _ready;
    private CancellationToken _lifetime;

    public override bool ContinueSession => false;
    public override void VerifyAvailable() { }
    public override IReadOnlyList<SessionSummary> ListSessions(string targetDirectory) => [];
    public override IReadOnlyList<(bool IsUser, string Text)> GetSessionMessages() => [];

    public override void Send(string sentence)
    {
        lock (_gate)
        {
            if (!_ready || _lifetime.IsCancellationRequested)
                throw new InvalidOperationException("Dummy assistant is not ready to accept speech.");
            _ready = false;
            if (!_input.Writer.TryWrite(sentence))
                throw new InvalidOperationException("Dummy assistant has stopped.");
        }
    }

    public override async IAsyncEnumerable<AgentEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("Dummy assistant can only run once.");
            _started = true;
            _lifetime = ct;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate) _ready = true;
            yield return AgentEvent.Ready.Instance;
            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return AgentEvent.TurnStart.Instance;
                yield return new AgentEvent.AssistantText(sentence);
                yield return AgentEvent.TurnComplete.Instance;
                ct.ThrowIfCancellationRequested();
                lock (_gate) _ready = true;
                yield return AgentEvent.Ready.Instance;
            }
        }
        finally
        {
            lock (_gate)
            {
                _ready = false;
                _input.Writer.TryComplete();
            }
        }
    }
}
