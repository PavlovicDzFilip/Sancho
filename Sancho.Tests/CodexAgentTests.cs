using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sancho.Console.Agents;
using Xunit;
namespace Sancho.Tests;
public class CodexAgentTests
{
    private static async Task<(List<AgentEvent> Events, string[] Arguments, string Input)> Run(string[] lines, string? resumed = null, int turns = 1, string sentence = "-literal prompt", bool startupFlood = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sancho-codex-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "script");
        var capture = Path.Combine(dir, "capture");
        var input = Path.Combine(dir, "input");
        var steps = new List<object> { new { kind = "capture", value = capture }, new { kind = "read", value = input } };
        if (startupFlood)
        {
            steps.InsertRange(1, Enumerable.Range(0, 100).SelectMany(_ => new object[] { new { kind = "stdout", value = new string('o', 1024) }, new { kind = "stderr", value = new string('e', 1024) } }));
        }
        steps.AddRange(lines.Select(value => (object)new { kind = "stdout", value }));
        await File.WriteAllLinesAsync(script, steps.Select(step => JsonSerializer.Serialize(step)), TestContext.Current.CancellationToken);
        var options = new AgentLaunchOptions { Executable = "dotnet", WorkingDirectory = dir, Instructions = "keep \"quotes\"\nnext line", PrefixArguments = new[] { Path.Combine(AppContext.BaseDirectory, "process-test-host", "Sancho.ProcessTestHost.dll"), "script", script } };
        var service = new CodexAgentService(resumed, NullLogger<CodexAgentService>.Instance, options);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = new List<AgentEvent>();
        var sent = 0;
        try
        {
            await foreach (var evt in service.RunAsync(lifetime.Token))
            {
                events.Add(evt);
                if (evt is AgentEvent.Ready)
                {
                    if (sent++ < turns) service.Send(sentence);
                    else lifetime.Cancel();
                }
            }
            using var captureDoc = JsonDocument.Parse(await File.ReadAllTextAsync(capture, TestContext.Current.CancellationToken));
            Assert.Equal(dir, captureDoc.RootElement.GetProperty("directory").GetString());
            return (events, captureDoc.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!).ToArray(), await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TwoTurnsResumeAndPreserveInstructionsAndLiteralInput()
    {
        var result = await Run(new[] { "{\"type\":\"thread.started\",\"thread_id\":\"thread-123\"}", "{\"type\":\"item.completed\",\"item\":{\"id\":\"a\",\"type\":\"agent_message\",\"text\":\"answer\"}}", "{\"type\":\"turn.completed\"}" }, turns: 2);
        Assert.Equal(new[] { "exec", "resume", "thread-123", "--json", "--dangerously-bypass-approvals-and-sandbox", "-c", "developer_instructions=" + JsonSerializer.Serialize("keep \"quotes\"\nnext line"), "-" }, result.Arguments);
        Assert.Equal(2, result.Events.OfType<AgentEvent.TurnComplete>().Count());
        Assert.Equal(2, result.Events.OfType<AgentEvent.AssistantText>().Count());
        Assert.Equal(2, result.Input.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(result.Events, e => e is AgentEvent.Error);
    }

    [Fact]
    public async Task ToolsErrorsDuplicatesAndMalformedEventsAreHandled()
    {
        var result = await Run(new[] {
            "[]", "not JSON", "{\"type\":\"thread.started\",\"thread_id\":\"thread-123\"}",
            "{\"type\":\"item.started\",\"item\":{\"id\":\"t\",\"type\":\"command_execution\",\"command\":\"echo hi\"}}",
            "{\"type\":\"item.updated\",\"item\":{\"id\":\"t\",\"type\":\"command_execution\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"t\",\"type\":\"command_execution\",\"exit_code\":1}}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"a\",\"type\":\"agent_message\",\"text\":\"answer\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"a\",\"type\":\"agent_message\",\"text\":\"answer\"}}",
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"quota\"}}" });
        Assert.Single(result.Events.OfType<AgentEvent.ToolUse>());
        Assert.Equal(new AgentEvent.ToolResult("t", true), Assert.Single(result.Events.OfType<AgentEvent.ToolResult>()));
        Assert.Single(result.Events.OfType<AgentEvent.AssistantText>());
        Assert.Contains(result.Events, e => e is AgentEvent.Error { Message: "quota" });
        Assert.Single(result.Events.OfType<AgentEvent.TurnComplete>());
    }

    [Fact]
    public async Task MissingThreadIdStopsInsteadOfStartingFreshConversation()
    {
        var result = await Run(new[] { "{\"type\":\"turn.completed\"}" });
        Assert.Single(result.Events.OfType<AgentEvent.Ready>());
        Assert.Single(result.Events.OfType<AgentEvent.TurnComplete>());
        Assert.Contains(result.Events, e => e is AgentEvent.Error error && error.Message.Contains("thread ID"));
    }
    [Fact]
    public void SessionsFilterMetadataByDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sancho-codex-sessions-" + Guid.NewGuid());
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            var target = Path.Combine(root, "target");
            var other = Path.Combine(root, "other");
            File.WriteAllText(Path.Combine(sessions, "rollout-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jsonl"), JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = target } }));
            File.WriteAllText(Path.Combine(sessions, "rollout-bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.jsonl"), JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = other } }));
            Assert.Equal(2, CodexAgentService.ListAllSessions(root).Count);
            Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Assert.Single(CodexAgentService.ListAllSessions(root, target)).Id);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("file_change")]
    [InlineData("mcp_tool_call")]
    [InlineData("web_search")]
    public async Task CompletedToolsWithoutStartStillProduceMatchingEvents(string toolType)
    {
        var line = JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "tool-7", type = toolType, status = "completed" } });
        var result = await Run(new[] { line, "{\"type\":\"turn.completed\"}" }, resumed: "existing-session");
        Assert.Equal(toolType, Assert.Single(result.Events.OfType<AgentEvent.ToolUse>()).Name);
        Assert.Equal(new AgentEvent.ToolResult("tool-7", false), Assert.Single(result.Events.OfType<AgentEvent.ToolResult>()));
        Assert.DoesNotContain(result.Events, e => e is AgentEvent.Error);
    }
    [Fact]
    public async Task MissingTerminalEventIsAnError()
    {
        var result = await Run(new[] { "{\"type\":\"thread.started\",\"thread_id\":\"thread-123\"}" });
        Assert.Contains(result.Events, e => e is AgentEvent.Error error && error.Message.Contains("terminal turn"));
        Assert.Single(result.Events.OfType<AgentEvent.TurnComplete>());
        Assert.Single(result.Events.OfType<AgentEvent.Ready>());
    }

    [Fact]
    public async Task MismatchedResumeThreadIsAnError()
    {
        var result = await Run(new[] { "{\"type\":\"thread.started\",\"thread_id\":\"wrong-thread\"}", "{\"type\":\"turn.completed\"}" }, resumed: "requested-thread");
        Assert.Contains(result.Events, e => e is AgentEvent.Error error && error.Message.Contains("different thread"));
        Assert.Single(result.Events.OfType<AgentEvent.TurnComplete>());
        Assert.Single(result.Events.OfType<AgentEvent.Ready>());
    }

    [Fact]
    public async Task StartupOutputAndLargeInputAreDrainedConcurrently()
    {
        var sentence = new string('x', 256 * 1024);
        var result = await Run(new[] { "{\"type\":\"thread.started\",\"thread_id\":\"thread-123\"}", "{\"type\":\"turn.completed\"}" }, sentence: sentence, startupFlood: true);
        Assert.Equal(sentence + Environment.NewLine, result.Input);
        Assert.DoesNotContain(result.Events, e => e is AgentEvent.Error);
        Assert.Single(result.Events.OfType<AgentEvent.TurnComplete>());
    }
}
