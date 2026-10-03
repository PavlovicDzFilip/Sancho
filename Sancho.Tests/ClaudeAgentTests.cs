using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

public class ClaudeAgentTests
{
    [Fact]
    public void NewConversationDoesNotReadAnotherSessionsNewestTranscript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-session-" + Guid.NewGuid());
        var project = Path.Combine(directory, "projects", SessionJson.EncodeProjectDirectory(directory));
        Directory.CreateDirectory(project);
        try
        {
            File.WriteAllText(Path.Combine(project, "other-session.jsonl"), "{\"type\":\"user\",\"message\":{\"content\":\"another conversation\"}}\n");
            var agent = new ClaudeCodeAgentService("unused", directory, null, NullLogger<ClaudeCodeAgentService>.Instance,
                new AgentLaunchOptions { WorkingDirectory = directory, Instructions = "test" });
            Assert.Empty(agent.GetSessionMessages());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"result\":\"turn limit\"}", "turn limit")]
    [InlineData("{\"type\":\"result\",\"is_error\":true,\"result\":\"failed\"}", "failed")]
    public void FailedResultsCompleteOnceAndUnsolicitedResultsDoNotComplete(string json, string error)
    {
        var agent = new ClaudeCodeAgentService("unused", null, null, NullLogger<ClaudeCodeAgentService>.Instance, new AgentLaunchOptions { Instructions = "test" });
        var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
        var method = typeof(ClaudeCodeAgentService).GetMethod("HandleStreamMessage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using var doc = JsonDocument.Parse(json);
        method.Invoke(agent, new object[] { doc.RootElement, channel.Writer });
        Assert.False(channel.Reader.TryRead(out _));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ClaudeCodeAgentService).GetField("_turnComplete", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(agent, completion);
        method.Invoke(agent, new object[] { doc.RootElement, channel.Writer });
        method.Invoke(agent, new object[] { doc.RootElement, channel.Writer });
        Assert.True(completion.Task.IsCompletedSuccessfully);
        Assert.True(channel.Reader.TryRead(out var first));
        Assert.Equal(error, Assert.IsType<AgentEvent.Error>(first).Message);
        Assert.True(channel.Reader.TryRead(out var second));
        Assert.IsType<AgentEvent.TurnComplete>(second);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public async Task PersistentTwoTurnProtocolPreservesNativePromptAndReportsFailedResult(string? resume)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-claude-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "protocol.jsonl");
        var capture = Path.Combine(directory, "launch.json");
        var input = Path.Combine(directory, "input.jsonl");
        var steps = new List<object>();
        void Step(string kind, string value) => steps.Add(new { kind, value });
        Step("capture", capture);

        Step("read", input);
        Step("stdout", "not json");
        Step("stdout", "[]");
        Step("stdout", "{\"type\":123}");
        Step("stdout", "{\"type\":\"unknown\"}");
        Step("stdout", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"first reply\"},{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"echo hi\"}}]}}");
        Step("stdout", "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"full-tool-id-123456789\",\"is_error\":true}]}}");
        Step("stdout", "{\"type\":\"result\",\"subtype\":\"success\",\"uuid\":\"turn-one-result\"}");
        Step("stdout", "{\"type\":\"result\",\"subtype\":\"success\",\"uuid\":\"turn-one-result\"}");
        Step("read", input);
        Step("stdout", "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"errors\":[\"tool failed\"]}");
        Step("delay", "60000");
        await File.WriteAllLinesAsync(script, steps.Select(s => JsonSerializer.Serialize(s)), TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var options = new AgentLaunchOptions
        {
            Executable = "dotnet", WorkingDirectory = directory, Instructions = "spoken \"instructions\"\nsecond line",
            PrefixArguments = new[] { Path.Combine(AppContext.BaseDirectory, "process-test-host", "Sancho.ProcessTestHost.dll"), "script", script }
        };
        var agent = new ClaudeCodeAgentService("unused", directory, resume, NullLogger<ClaudeCodeAgentService>.Instance, options);
        var events = new List<AgentEvent>();
        var sent = 0;
        try
        {
            await foreach (var evt in agent.RunAsync(lifetime.Token))
            {
                events.Add(evt);
                if (evt is AgentEvent.Ready)
                {
                    if (sent < 2) agent.Send("sentence " + ++sent);
                    else lifetime.Cancel();
                }
            }
            Assert.False(timeout.IsCancellationRequested);
            Assert.Equal(2, events.OfType<AgentEvent.TurnStart>().Count());
            Assert.Equal(2, events.OfType<AgentEvent.TurnComplete>().Count());
            Assert.Single(events.OfType<AgentEvent.Error>(), e => e.Message == "tool failed");
            Assert.Contains(events, e => e is AgentEvent.AssistantText { Text: "first reply" });
            Assert.Contains(events, e => e is AgentEvent.ToolUse { Name: "Bash", InputPreview: "echo hi" });
            Assert.Contains(events, e => e is AgentEvent.ToolResult { ToolId: "full-tool-id-123456789", IsError: true });
            using var launch = JsonDocument.Parse(await File.ReadAllTextAsync(capture, timeout.Token));
            var arguments = launch.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).ToArray();
            Assert.Contains("--append-system-prompt", arguments);
            Assert.DoesNotContain("--system-prompt", arguments);
            Assert.Contains(options.Instructions, arguments);
            Assert.Contains("bypassPermissions", arguments);
            Assert.Equal(directory, launch.RootElement.GetProperty("directory").GetString());
            Assert.Contains(resume is null ? "--session-id" : "--resume", arguments);
            if (resume is not null) Assert.Contains(resume, arguments);
            var received = await File.ReadAllLinesAsync(input, timeout.Token);
            Assert.Equal(2, received.Length);
            Assert.Equal("sentence 2", JsonDocument.Parse(received[1]).RootElement.GetProperty("message").GetProperty("content").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }
}


