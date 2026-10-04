using Sancho.Console.Agents;
using Sancho.Console.Orchestration;
using Xunit;

namespace Sancho.Tests;

public class DummyAgentTests
{
    [Fact]
    public async Task EchoesTwoSpeechTurnsExactlyThroughSharedQueue()
    {
        var agent = new DummyAgentService();
        var queue = new AgentInputQueue();
        queue.Add("  first recognized sentence!  ");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = agent.RunAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        var replies = new List<string>();
        var completed = 0;
        var sent = 0;
        while (await events.MoveNextAsync())
        {
            switch (events.Current)
            {
                case AgentEvent.Ready:
                    if (completed == 2)
                    {
                        Assert.Equal(new[] { "  first recognized sentence!  ", "follow up one" + Environment.NewLine + "follow up two" }, replies);
                        Assert.Empty(queue.Snapshot());
                        return;
                    }
                    queue.MarkReady();
                    Assert.NotNull(queue.TrySend(agent.Send));
                    sent++;
                    Assert.Throws<InvalidOperationException>(() => agent.Send("must not overlap"));
                    break;
                case AgentEvent.TurnStart when sent == 1:
                    queue.Add("follow up one");
                    queue.Add("follow up two");
                    Assert.Null(queue.TrySend(agent.Send));
                    break;
                case AgentEvent.AssistantText(var text): replies.Add(text); break;
                case AgentEvent.TurnComplete: completed++; break;
                case AgentEvent.Error(var message): Assert.Fail(message); break;
            }
        }
        Assert.Fail("Dummy assistant stopped before completing both speech turns.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDisposalStopsAcceptingInput(bool cancel)
    {
        var agent = new DummyAgentService();
        Assert.Throws<InvalidOperationException>(() => agent.Send("before startup"));
        using var lifetime = new CancellationTokenSource();
        var events = agent.RunAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<AgentEvent.Ready>(events.Current);
        if (cancel)
        {
            lifetime.Cancel();
            Assert.Throws<InvalidOperationException>(() => agent.Send("after cancellation"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync());
        }
        await events.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => agent.Send("after shutdown"));
        Assert.False(agent.ContinueSession);
        Assert.Empty(agent.ListSessions("unused"));
        Assert.Empty(agent.GetSessionMessages());
        agent.VerifyAvailable();
    }
}
