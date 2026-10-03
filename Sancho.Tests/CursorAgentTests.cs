using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

public class CursorAgentTests
{
    [Fact]
    public void TranscriptResolutionUsesOnlyKnownSessionEvenWhenAnotherIsNewer()
    {
        var root = Path.Combine(Path.GetTempPath(), "sancho-cursor-transcripts-" + Guid.NewGuid());
        var target = Path.Combine(root, "workspace");
        var transcripts = Path.Combine(root, "projects", SessionJson.EncodeProjectDirectory(target), "agent-transcripts");
        try
        {
            var current = Path.Combine(transcripts, "captured-id", "captured-id.jsonl");
            var unrelated = Path.Combine(transcripts, "unrelated", "unrelated.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
            File.WriteAllText(current, "current conversation");
            File.WriteAllText(unrelated, "unrelated conversation");
            File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddDays(-1));
            Assert.Equal(current, CursorAgentService.ResolveSessionFile(root, target, "captured-id"));
            Assert.Null(CursorAgentService.ResolveSessionFile(root, target, null));
            Assert.Null(CursorAgentService.ResolveSessionFile(root, target, "missing-id"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RefusesUnexpectedSessionChange()
    {
        await Exercise(new[]
        {
            "{\"type\":\"system\",\"session_id\":\"expected\"}",
            "{\"type\":\"assistant\",\"session_id\":\"different\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"wrong conversation\"}]}}"
        }, 0, 1, (events, _, _) =>
        {
            Assert.Contains(events, e => e is AgentEvent.Error error && error.Message.Contains("different session ID"));
            Assert.Empty(events.OfType<AgentEvent.AssistantText>());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TwoTurnsPreserveSessionInstructionsWorkspaceAndToolResults()
    {
        var lines = new[]
        {
            "{\"type\":\"assistant\",\"session_id\":\"cursor-session\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"answer\"}]}}",
            "{\"type\":\"tool_call\",\"subtype\":\"started\",\"call_id\":\"read-1\",\"tool_call\":{\"readToolCall\":{\"args\":{\"path\":\"README.md\"}}}}",
            "{\"type\":\"tool_call\",\"subtype\":\"completed\",\"call_id\":\"read-1\",\"tool_call\":{\"readToolCall\":{\"result\":{\"success\":{}}}}}",
            "{\"type\":\"tool_call\",\"subtype\":\"completed\",\"call_id\":\"read-2\",\"tool_call\":{\"readToolCall\":{\"result\":{\"error\":\"missing file\"}}}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"answer\"}"
        };
        await Exercise(lines, 0, 2, async (events, directory, capture) =>
        {
            Assert.Equal(new[] { "answer", "answer" }, events.OfType<AgentEvent.AssistantText>().Select(e => e.Text));
            Assert.Equal(2, events.OfType<AgentEvent.ToolUse>().Count());
            Assert.Contains(events, e => e is AgentEvent.ToolResult { ToolId: "read-1", IsError: false });
            Assert.Contains(events, e => e is AgentEvent.ToolResult { ToolId: "read-2", IsError: true });
            Assert.DoesNotContain(events, e => e is AgentEvent.Error);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(capture));
            var args = doc.RootElement.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("--force", args);
            Assert.Contains("--trust", args);
            Assert.Contains("--approve-mcps", args);
            Assert.Contains("disabled", args);
            Assert.Contains("--resume", args);
            Assert.Contains("cursor-session", args);
            Assert.Contains(args, a => a is not null && a.Contains("<sancho_instructions>\nproject rules") && a.EndsWith("--dangerous spoken text"));
            Assert.Equal(directory, doc.RootElement.GetProperty("directory").GetString());
        });
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"fallback reply\",\"session_id\":\"id\"}", 0, false)]
    [InlineData("{\"type\":\"result\",\"is_error\":true,\"result\":\"denied\"}", 0, true)]
    [InlineData("{\"type\":\"result\",\"subtype\":\"error\"}", 0, true)]
    [InlineData("not json", 0, true)]
    [InlineData("{}", 9, true)]
    [InlineData("[]", 0, true)]
    [InlineData("", 0, true)]
    public async Task TerminalFallbackAndFailuresCompleteExactlyOnce(string line, int exit, bool error)
    {
        await Exercise(new[] { line }, exit, 1, (events, _, _) =>
        {
            Assert.Equal(error, events.OfType<AgentEvent.Error>().Any());
            if (!error) Assert.Equal("fallback reply", Assert.Single(events.OfType<AgentEvent.AssistantText>()).Text);
            return Task.CompletedTask;
        });
    }

    private static async Task Exercise(string[] lines, int exit, int turns,
        Func<List<AgentEvent>, string, string, Task> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-cursor-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "script.jsonl");
        var capture = Path.Combine(directory, "arguments.json");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, ".sancho.md"), "project rules");
            var steps = new List<string> { JsonSerializer.Serialize(new { kind = "capture", value = capture }) };
            steps.AddRange(lines.Select(line => JsonSerializer.Serialize(new { kind = "stdout", value = line })));
            steps.Add(JsonSerializer.Serialize(new { kind = "exit", value = exit.ToString() }));
            await File.WriteAllLinesAsync(script, steps);
            var options = new AgentLaunchOptions
            {
                WorkingDirectory = directory, Executable = "dotnet",
                PrefixArguments = new[] { Path.Combine(AppContext.BaseDirectory, "process-test-host", "Sancho.ProcessTestHost.dll"), "script", script }
            };
            var agent = new CursorAgentService(null, NullLogger<CursorAgentService>.Instance, options);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var events = new List<AgentEvent>();
            var sent = 0;
            await foreach (var evt in agent.RunAsync(timeout.Token))
            {
                events.Add(evt);
                if (evt is AgentEvent.Ready)
                {
                    if (sent == turns) break;
                    sent++;
                    agent.Send("--dangerous spoken text");
                }
            }
            Assert.False(timeout.IsCancellationRequested);
            Assert.Equal(turns, events.OfType<AgentEvent.TurnComplete>().Count());
            await check(events, directory, capture);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
