using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

public class HermesAgentTests
{
    [Fact]
    public async Task TwoTurnsPreserveSessionInstructionsWorkspaceAndToolResults()
    {
        var lines = new[]
        {
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"20261003_120000_abcd1234\"}",
            "{\"type\":\"text\",\"text\":\"answer\"}",
            "{\"type\":\"tool_use\",\"name\":\"read_file\",\"input\":{\"path\":\"README.md\"}}",
            "{\"type\":\"tool_result\",\"name\":\"read_file\",\"is_error\":false}",
            "{\"type\":\"tool_result\",\"name\":\"missing_file\",\"is_error\":true}",
            "{\"type\":\"result\",\"exit_code\":0,\"text\":\"answer\",\"session_id\":\"20261003_120000_abcd1234\"}"
        };
        await Exercise(lines, 0, 2, async (events, directory, capture) =>
        {
            Assert.Equal(new[] { "answer", "answer" }, events.OfType<AgentEvent.AssistantText>().Select(e => e.Text));
            Assert.Equal(2, events.OfType<AgentEvent.ToolUse>().Count());
            Assert.Contains(events, e => e is AgentEvent.ToolResult { ToolId: "read_file", IsError: false });
            Assert.Contains(events, e => e is AgentEvent.ToolResult { ToolId: "missing_file", IsError: true });
            Assert.DoesNotContain(events, e => e is AgentEvent.Error);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(capture));
            var args = doc.RootElement.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("--yolo", args);
            Assert.Contains("--oneshot", args);
            Assert.Contains("stream-json", args);
            Assert.Contains("--in", args);
            Assert.Contains("--resume", args);
            Assert.Contains("20261003_120000_abcd1234", args);
            Assert.Contains(args, a => a is not null && a.Contains("<sancho_instructions>\nproject rules") && a.EndsWith("--dangerous spoken text"));
            Assert.Equal(directory, doc.RootElement.GetProperty("directory").GetString());
        });
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"exit_code\":0,\"text\":\"fallback reply\",\"session_id\":\"id\"}", 0, false)]
    [InlineData("{\"type\":\"result\",\"exit_code\":1,\"error\":\"denied\"}", 0, true)]
    [InlineData("{\"type\":\"result\",\"error\":\"failed\"}", 0, true)]
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

    [Fact]
    public async Task MissingSessionIdFailsInsteadOfStartingAnotherConversation()
    {
        await Exercise(new[] { "{\"type\":\"result\",\"exit_code\":0,\"text\":\"reply\"}" }, 0, 1,
            (events, _, _) =>
            {
                Assert.Contains(events, e => e is AgentEvent.Error error && error.Message.Contains("session ID"));
                Assert.Single(events.OfType<AgentEvent.Ready>());
                return Task.CompletedTask;
            });
    }

    [Fact]
    public void SessionListUsesNativeIdsAndKeepsCliOrder()
    {
        var sessions = HermesAgentService.ParseSessionList("Header\n|20261003_120000_abcd12| latest |\n|20261002_100000_1234abcd| older |\n");
        Assert.Equal(new[] { "20261003_120000_abcd12", "20261002_100000_1234abcd" }, sessions.Select(s => s.Id));
    }

    [Fact]
    public void ExportReadsSessionMessageArraysAndSkipsToolsAndMalformedRecords()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, new[]
            {
                "not json", "[]",
                "{\"session_id\":\"20261003_120000_abcd12\",\"messages\":[{\"role\":\"system\",\"content\":\"rules\"},{\"role\":\"user\",\"content\":\"hello\"},{\"role\":\"tool\",\"content\":\"hidden\"},{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"answer\"}]}]}"
            });
            Assert.Equal(new[] { (true, "hello"), (false, "answer") }, HermesAgentService.ReadExport(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task InitiallyResumedSessionUsesExactNativeId()
    {
        const string id = "20261003_120000_abcd12";
        await Exercise(new[] { JsonSerializer.Serialize(new { type = "result", exit_code = 0, session_id = id, text = "continued" }) }, 0, 1,
            async (events, _, capture) =>
            {
                Assert.DoesNotContain(events, e => e is AgentEvent.Error);
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(capture));
                var args = doc.RootElement.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()).ToArray();
                Assert.Equal(id, args[Array.IndexOf(args, "--resume") + 1]);
            }, id);
    }

    [Fact]
    public async Task CompressionReportedSessionIdIsUsedForNextTurn()
    {
        const string originalId = "20261003_120000_abcd12";
        const string compressedId = "20261003_120500_1234ab";
        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "system", subtype = "init", session_id = originalId }),
            JsonSerializer.Serialize(new { type = "result", exit_code = 0, session_id = compressedId, text = "compressed" })
        };
        await Exercise(lines, 0, 2, async (events, _, capture) =>
        {
            Assert.DoesNotContain(events, e => e is AgentEvent.Error);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(capture));
            var args = doc.RootElement.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(compressedId, args[Array.IndexOf(args, "--resume") + 1]);
        }, originalId);
    }

    private static async Task Exercise(string[] lines, int exit, int turns,
        Func<List<AgentEvent>, string, string, Task> check, string? resumeSessionId = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-hermes-" + Guid.NewGuid());
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
            var agent = new HermesAgentService(resumeSessionId, NullLogger<HermesAgentService>.Instance, options);
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



