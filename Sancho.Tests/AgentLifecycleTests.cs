using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

public class AgentLifecycleTests
{
    private static AgentService Create(string backend, AgentLaunchOptions options) => backend switch
    {
        "claude" => new ClaudeCodeAgentService("unused", null, null, NullLogger<ClaudeCodeAgentService>.Instance, options),
        "codex" => new CodexAgentService(null, NullLogger<CodexAgentService>.Instance, options),
        "cursor" => new CursorAgentService(null, NullLogger<CursorAgentService>.Instance, options),
        _ => new HermesAgentService("resumed-test", NullLogger<HermesAgentService>.Instance, options)
    };

    private static AgentLaunchOptions Options(params string[] arguments) => new()
    {
        Executable = "dotnet",
        Instructions = "test instructions",
        PrefixArguments = new[] { Path.Combine(AppContext.BaseDirectory, "process-test-host", "Sancho.ProcessTestHost.dll") }.Concat(arguments).ToArray()
    };

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("hermes")]
    public async Task CallerArgumentsReachEachBackendVerbatim(string backend)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-forward-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "script.jsonl");
        var capture = Path.Combine(directory, "arguments.json");
        string[] arguments = ["--model", "caller \"model\"", "", "$(literal)", "--", "tail"];
        var completion = backend switch
        {
            "claude" => "{\"type\":\"result\",\"subtype\":\"success\"}",
            "codex" => "{\"type\":\"turn.completed\"}",
            _ => "{\"type\":\"result\",\"session_id\":\"resumed-test\",\"exit_code\":0}"
        };
        var steps = new List<object> { new { kind = "capture", value = capture } };
        if (backend is "claude" or "codex") steps.Add(new { kind = "read", value = "" });
        steps.Add(new { kind = "stdout", value = completion });
        if (backend == "claude") steps.Add(new { kind = "delay", value = "60000" });
        await File.WriteAllLinesAsync(script, steps.Select(s => JsonSerializer.Serialize(s)), TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = Options("script", script) with { WorkingDirectory = directory, AgentArguments = arguments };
        var agent = Create(backend, options);
        var sent = false;
        try
        {
            await foreach (var evt in agent.RunAsync(timeout.Token))
            {
                if (evt is not AgentEvent.Ready) continue;
                if (sent) break;
                sent = true;
                agent.Send("spoken input");
            }
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(capture, timeout.Token));
            var actual = json.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).ToArray();
            var start = Array.IndexOf(actual, "--model");
            Assert.True(start >= 0);
            Assert.Equal(arguments, actual.Skip(start).Take(arguments.Length));
            Assert.Equal(directory, json.RootElement.GetProperty("directory").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("hermes")]
    public async Task StartupFailureReportsErrorAndClosesStream(string backend)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var agent = Create(backend, Options() with { Executable = "sancho-nonexistent-" + Guid.NewGuid() });
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(timeout.Token))
        {
            events.Add(evt);
            if (evt is AgentEvent.Ready) agent.Send("first");
        }
        Assert.False(timeout.IsCancellationRequested);
        Assert.Contains(events, evt => evt is AgentEvent.Error);
        Assert.Throws<InvalidOperationException>(() => agent.Send("after exit"));
    }

    [Theory]
    [InlineData("claude", false)]
    [InlineData("codex", false)]
    [InlineData("cursor", false)]
    [InlineData("hermes", false)]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("cursor", true)]
    [InlineData("hermes", true)]
    public async Task CancellationOrEarlyDisposalTerminatesActiveProcess(string backend, bool disposeEarly)
    {
        var script = Path.GetTempFileName();
        var pidFile = script + ".pid";
        var childPidFile = script + ".child.pid";
        await File.WriteAllLinesAsync(script, new[]
        {
            JsonSerializer.Serialize(new { kind = "pid", value = pidFile }),
            JsonSerializer.Serialize(new { kind = "child", value = childPidFile }),
            JsonSerializer.Serialize(new { kind = "delay", value = "60000" })
        }, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var agent = Create(backend, Options("script", script));
        var stream = agent.RunAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        try
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.IsType<AgentEvent.Ready>(stream.Current);
            agent.Send("first");
            Assert.Throws<InvalidOperationException>(() => agent.Send("duplicate"));
            var pid = await ReadPublishedPidAsync(pidFile, timeout.Token);
            var childPid = await ReadPublishedPidAsync(childPidFile, timeout.Token);
            if (!disposeEarly) lifetime.Cancel();
            await stream.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            Assert.False(IsRunning(pid));
            // WaitForExit waits for the root, not descendants. Tree termination is
            // asynchronous; Unix descendants may briefly be zombies awaiting reaping.
            var descendantDeadline = Stopwatch.StartNew();
            while (IsRunning(childPid) && descendantDeadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20, timeout.Token);
            Assert.False(IsRunning(childPid), $"Child {childPid} survived process-tree termination.");
            Assert.Throws<InvalidOperationException>(() => agent.Send("after stop"));
        }
        finally
        {
            lifetime.Cancel();
            await stream.DisposeAsync();
            File.Delete(script);
            File.Delete(pidFile);
            File.Delete(childPidFile);
        }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("hermes")]
    public async Task IdleCancellationClosesStream(string backend)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var agent = Create(backend, Options("wait"));
        await using var stream = agent.RunAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        Assert.True(await stream.MoveNextAsync());
        Assert.IsType<AgentEvent.Ready>(stream.Current);
        lifetime.Cancel();
        while (await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token)) { }
        Assert.False(timeout.IsCancellationRequested);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("hermes")]
    public async Task ConcurrentOutputAndStderrFloodDoesNotDeadlock(string backend)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var agent = Create(backend, Options("flood"));
        var sent = false;
        var completed = false;
        await foreach (var evt in agent.RunAsync(lifetime.Token))
        {
            if (evt is AgentEvent.Ready)
            {
                if (!sent) { sent = true; agent.Send("first"); }
                else { completed = true; lifetime.Cancel(); }
            }
            if (backend == "claude" && evt is AgentEvent.Error) completed = true;
            // The deliberately non-protocol flood can fail a turn. It must still
            // finish and close or become ready, rather than deadlocking its pipes.
            if (evt is AgentEvent.TurnComplete) completed = true;
        }
        Assert.False(timeout.IsCancellationRequested);
        Assert.True(completed);
    }

    [Fact]
    public async Task ClaudePreservesBufferedFinalReplyResultAndStderrOnImmediateExit()
    {
        var script = Path.GetTempFileName();
        var steps = new List<string>
        {
            JsonSerializer.Serialize(new { kind = "read", value = "" })
        };
        for (var index = 0; index < 128; index++)
            steps.Add(JsonSerializer.Serialize(new { kind = "stdout", value = new string('x', 1024) }));
        steps.Add(JsonSerializer.Serialize(new { kind = "stdout", value = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"final buffered reply\"}]}}" }));
        steps.Add(JsonSerializer.Serialize(new { kind = "stdout", value = "{\"type\":\"result\",\"subtype\":\"success\"}" }));
        steps.Add(JsonSerializer.Serialize(new { kind = "stderr", value = "final stderr diagnostic" }));
        steps.Add(JsonSerializer.Serialize(new { kind = "exit", value = "0" }));
        await File.WriteAllLinesAsync(script, steps, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var agent = Create("claude", Options("script", script));
            var events = new List<AgentEvent>();
            var sent = false;
            await foreach (var evt in agent.RunAsync(timeout.Token))
            {
                events.Add(evt);
                if (!sent && evt is AgentEvent.Ready) { agent.Send("first"); sent = true; }
            }
            Assert.False(timeout.IsCancellationRequested);
            Assert.Contains(events, evt => evt is AgentEvent.AssistantText text && text.Text == "final buffered reply");
            Assert.Contains(events, evt => evt is AgentEvent.Status status && status.Message == "final stderr diagnostic");
            Assert.Single(events.OfType<AgentEvent.TurnComplete>());
            Assert.IsType<AgentEvent.Error>(events.Last());
        }
        finally { File.Delete(script); }
    }

    private static async Task<int> ReadPublishedPidAsync(string path, CancellationToken ct)
    {
        // File creation precedes completion of the asynchronous write. Wait for
        // the published PID, not merely the directory entry, before cancelling.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path)
                    && int.TryParse(await File.ReadAllTextAsync(path, ct), out var pid) && pid > 0)
                    return pid;
            }
            catch (IOException) { } // The writer can briefly hold an exclusive file handle.
            await Task.Delay(20, ct);
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
