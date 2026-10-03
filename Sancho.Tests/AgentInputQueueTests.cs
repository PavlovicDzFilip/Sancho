using Sancho.Console.Orchestration;
using Sancho.Console.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace Sancho.Tests;

public class AgentInputQueueTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("hermes")]
    public async Task SharedSpeechQueueSendsTwoTurnsThroughRealBackendProcess(string backend)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-voice-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "script");
        var capture = Path.Combine(directory, "capture");
        var input = Path.Combine(directory, "input");
        var steps = new List<object>();
        void Step(string kind, string value) => steps.Add(new { kind, value });
        Step("capture", capture);
        if (backend is "claude" or "codex") Step("read", input);
        if (backend == "codex") Step("read", input); // stdin closes at EOF; capture the second batched line too
        string result = backend switch
        {
            "claude" => "{\"type\":\"result\",\"subtype\":\"success\"}",
            "codex" => "{\"type\":\"turn.completed\"}",
            "cursor" => "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"voice-test\"}",
            _ => "{\"type\":\"result\",\"exit_code\":0,\"session_id\":\"20261003_120000_abcd1234\"}"
        };
        if (backend == "codex") Step("stdout", "{\"type\":\"thread.started\",\"thread_id\":\"voice-test\"}");
        Step("stdout", result);
        if (backend == "claude")
        {
            Step("read", input);
            Step("stdout", result);
            Step("delay", "60000");
        }
        await File.WriteAllLinesAsync(script, steps.Select(s => JsonSerializer.Serialize(s)), TestContext.Current.CancellationToken);
        var options = new AgentLaunchOptions { Executable = "dotnet", WorkingDirectory = directory, Instructions = "test", PrefixArguments = [Path.Combine(AppContext.BaseDirectory, "process-test-host", "Sancho.ProcessTestHost.dll"), "script", script] };
        AgentService agent = backend switch
        {
            "claude" => new ClaudeCodeAgentService("unused", null, null, NullLogger<ClaudeCodeAgentService>.Instance, options),
            "codex" => new CodexAgentService(null, NullLogger<CodexAgentService>.Instance, options),
            "cursor" => new CursorAgentService(null, NullLogger<CursorAgentService>.Instance, options),
            _ => new HermesAgentService(null, NullLogger<HermesAgentService>.Instance, options)
        };
        var queue = new AgentInputQueue();
        queue.Add("first spoken request");
        var sent = new List<string>();
        var completed = 0;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var evt in agent.RunAsync(lifetime.Token))
            {
                Assert.False(evt is AgentEvent.Error, evt.ToString());
                if (evt is AgentEvent.TurnComplete) completed++;
                if (evt is AgentEvent.Ready)
                {
                    queue.MarkReady();
                    var batch = queue.TrySend(agent.Send);
                    if (batch is not null) sent.Add(batch);
                    else if (sent.Count == 2) lifetime.Cancel();
                }
                if (evt is AgentEvent.TurnStart && sent.Count == 1)
                {
                    queue.Add("follow up one");
                    queue.Add("follow up two");
                    Assert.Null(queue.TrySend(agent.Send));
                }
            }
            Assert.Equal(new[] { "first spoken request", "follow up one" + Environment.NewLine + "follow up two" }, sent);
            Assert.Empty(queue.Snapshot());
            Assert.Equal(2, completed);
            var recorded = backend is "claude" or "codex" ? await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken) : await File.ReadAllTextAsync(capture, TestContext.Current.CancellationToken);
            Assert.Contains("follow up one", recorded);
            Assert.Contains("follow up two", recorded);
        }
        finally { queue.Stop(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void SpeechDuringBusyTurnBatchesIntoExactlyOneNextTurn()
    {
        var queue = new AgentInputQueue();
        var sent = new List<string>();
        queue.Add("first task");
        Assert.Null(queue.TrySend(sent.Add));
        queue.MarkReady();
        Assert.Equal("first task", queue.TrySend(sent.Add));
        queue.Add("follow up one");
        queue.Add("follow up two");
        Assert.Null(queue.TrySend(sent.Add));
        Assert.Equal(new[] { "follow up one", "follow up two" }, queue.Snapshot());
        queue.MarkReady();
        Assert.Equal("follow up one" + Environment.NewLine + "follow up two", queue.TrySend(sent.Add));
        Assert.Null(queue.TrySend(sent.Add));
        Assert.Equal(2, sent.Count);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public void RejectedSendRetainsSpeechAndRequiresFreshReady()
    {
        var queue = new AgentInputQueue();
        queue.Add("keep this request");
        queue.MarkReady();
        Assert.Throws<InvalidOperationException>(() => queue.TrySend(_ => throw new InvalidOperationException("closed")));
        Assert.Equal(new[] { "keep this request" }, queue.Snapshot());
        Assert.Null(queue.TrySend(_ => Assert.Fail("must wait for Ready")));
        queue.MarkReady();
        Assert.Equal("keep this request", queue.TrySend(_ => { }));
    }

    [Fact]
    public void ClosedBackendKeepsPendingTranscriptAndCannotResumeSending()
    {
        var queue = new AgentInputQueue();
        queue.Add("pending request");
        queue.Stop();
        queue.MarkReady();
        queue.Add("speech after capture stopped");
        Assert.Null(queue.TrySend(_ => Assert.Fail("stopped backend must not receive input")));
        Assert.Equal(new[] { "pending request" }, queue.Snapshot());
    }
}
