using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Agents;

/// <summary>
/// Agent backend for OpenAI's Codex CLI. One process per turn using
/// <c>codex exec --json</c> (machine-readable JSONL events). The first
/// turn's <c>thread.started</c> event carries the session id, which later
/// turns resume via <c>codex exec resume &lt;id&gt; --json</c>.
/// </summary>
public sealed class CodexAgentService : AgentService
{
    private const string Executable = "codex";

    // Session ids are UUIDs, embedded in rollout file names and session_meta.
    private static readonly Regex SessionIdPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private readonly string? _resumeSessionId;
    private readonly ILogger<CodexAgentService> _logger;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();

    private string? _sessionId;
    private int _ready;
    private readonly AgentLaunchOptions _launchOptions;

    public CodexAgentService(string? resumeSessionId, ILogger<CodexAgentService> logger, AgentLaunchOptions? launchOptions = null)
    {
        _resumeSessionId = resumeSessionId;
        _sessionId = resumeSessionId;
        _logger = logger;
        _launchOptions = launchOptions ?? new AgentLaunchOptions();
    }

    /// <inheritdoc />
    public override bool ContinueSession => _resumeSessionId is not null;

    /// <inheritdoc />
    public override void Send(string sentence)
    {
        if (Interlocked.CompareExchange(ref _ready, 0, 1) != 1)
            throw new InvalidOperationException($"{Executable} is not ready to accept input.");
        if (!_input.Writer.TryWrite(sentence))
            throw new InvalidOperationException("Agent event stream has stopped.");
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<AgentEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var events = Channel.CreateUnbounded<AgentEvent>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var worker = WorkerAsync(events.Writer, lifetime.Token);

        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync())
                yield return evt;
        }
        finally
        {
            lifetime.Cancel();
            await worker;
        }
    }

    /// <summary>The codex session store: <c>~/.codex</c>.</summary>
    public static string DefaultSessionRoot() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <inheritdoc />
    public override IReadOnlyList<AgentService.SessionSummary> ListSessions(string targetDirectory) =>
        ListAllSessions(DefaultSessionRoot(), targetDirectory);

    /// <summary>
    /// Lists stored sessions from the rollout files under
    /// <c>~/.codex/sessions/&lt;year&gt;/&lt;month&gt;/&lt;day&gt;/</c>,
    /// newest first, optionally filtering session metadata by working directory.
    /// </summary>
    public static IReadOnlyList<AgentService.SessionSummary> ListAllSessions(string sessionRoot, string? targetDirectory = null)
    {
        var dir = Path.Combine(sessionRoot, "sessions");
        if (!Directory.Exists(dir))
            return Array.Empty<AgentService.SessionSummary>();

        return Directory.GetFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Where(file => MatchesDirectory(file, targetDirectory))
            .Select(file =>
            {
                var match = SessionIdPattern.Match(Path.GetFileNameWithoutExtension(file));
                var id = match.Success ? match.Value : Path.GetFileNameWithoutExtension(file);
                return new AgentService.SessionSummary(
                    id,
                    File.GetLastWriteTime(file),
                    null,
                    SessionJson.GetSessionPreview(file, SessionJson.Format.Codex));
            })
            .OrderByDescending(s => s.LastActivity)
            .ToList();
    }

    /// <inheritdoc />
    public override IReadOnlyList<(bool IsUser, string Text)> GetSessionMessages()
    {
        var file = ResolveSessionFile();
        return file is null
            ? Array.Empty<(bool IsUser, string Text)>()
            : SessionJson.ReadMessages(file, SessionJson.Format.Codex);
    }

    /// <inheritdoc />
    public override void VerifyAvailable() => VerifyAvailable(Executable);

    /// <summary>Checks the CLI and its login status, including OS credential-store authentication.</summary>
    public static void VerifyAvailable(string executable = Executable)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("Could not start codex.");
            var versionOutput = proc.StandardOutput.ReadToEndAsync();
            var versionError = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5_000))
            {
                proc.Kill(entireProcessTree: true);
                throw new InvalidOperationException("codex version check timed out.");
            }
            Task.WhenAll(versionOutput, versionError).GetAwaiter().GetResult();
            if (proc.ExitCode != 0) throw new InvalidOperationException("codex is not installed. Install the Codex CLI from OpenAI and try again.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "Could not find `codex`. Install the Codex CLI from OpenAI and try again.");
        }

        using var login = Process.Start(new ProcessStartInfo(executable, "login status")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
        if (login is null) throw new InvalidOperationException("Could not check codex login status.");
        var output = login.StandardOutput.ReadToEndAsync();
        var error = login.StandardError.ReadToEndAsync();
        if (!login.WaitForExit(5_000))
        {
            login.Kill(entireProcessTree: true);
            throw new InvalidOperationException("codex login status timed out.");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (login.ExitCode != 0) throw new InvalidOperationException("codex is not logged in. Run `codex login` and try again.");
    }

    // ── Turn worker ────────────────────────────────────────────────

    private async Task WorkerAsync(ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        var turnActive = false;
        try
        {
            Interlocked.Exchange(ref _ready, 1);
            writer.TryWrite(AgentEvent.Ready.Instance);

            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                Interlocked.Exchange(ref _ready, 0);
                turnActive = true;
                writer.TryWrite(AgentEvent.TurnStart.Instance);
                await RunTurnAsync(writer, sentence, ct);
                writer.TryWrite(AgentEvent.TurnComplete.Instance);
                turnActive = false;
                Interlocked.Exchange(ref _ready, 1);
                writer.TryWrite(AgentEvent.Ready.Instance);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            writer.TryWrite(new AgentEvent.Error($"{Executable} failed: {ex.Message}"));
            if (turnActive)
                writer.TryWrite(AgentEvent.TurnComplete.Instance);
        }
        finally
        {
            Interlocked.Exchange(ref _ready, 0);
            _input.Writer.TryComplete();
            writer.TryComplete();
        }
    }

    private async Task RunTurnAsync(ChannelWriter<AgentEvent> writer, string sentence, CancellationToken ct)
    {
        var psi = _launchOptions.CreateStartInfo(Executable, []);
        psi.ArgumentList.Add("exec");
        if (_sessionId is not null)
        {
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add(_sessionId);
        }

        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
        var instructions = _launchOptions.ReadInstructions();
        if (instructions.Length > 0)
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("developer_instructions=" + JsonSerializer.Serialize(instructions));
        }
        psi.ArgumentList.Add("-");
        var emitted = new HashSet<string>();
        var started = new HashSet<string>();
        var terminalReceived = false;

        await using var owned = OwnedAgentProcess.Start(psi, ct);
        var process = owned.Process;
        var inputTask = WriteInputAsync(process, sentence, ct);
        _logger.LogDebug("→ {Executable}: {Text}", Executable, sentence);

        var stderrTask = ReadStderrAsync(process, writer, ct);
        try
        {

            using var reader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                _logger.LogDebug("← {Executable}: {Line}", Executable, line);
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    HandleEvent(doc.RootElement, writer, emitted, started);
                    terminalReceived |= String(doc.RootElement, "type") is "turn.completed" or "turn.failed";
                }
                catch (JsonException)
                {
                    writer.TryWrite(new AgentEvent.Status(line, AgentStatusKind.Info));
                }
            }

            await inputTask;
            await process.WaitForExitAsync(ct);
            await stderrTask;
            if (_sessionId is null) throw new InvalidOperationException("Codex did not provide a thread ID; refusing to start an unrelated conversation.");

            if (process.ExitCode == 0 && !terminalReceived)
                throw new InvalidOperationException("Codex exited without a terminal turn event.");
            if (process.ExitCode != 0 && !ct.IsCancellationRequested)
                writer.TryWrite(new AgentEvent.Error($"{Executable} exited with code {process.ExitCode}"));
        }
        finally
        {
            await owned.StopAsync();
            await stderrTask;
            try { await inputTask; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task WriteInputAsync(Process process, string sentence, CancellationToken ct)
    {
        try { await process.StandardInput.WriteAsync(sentence.AsMemory(), ct); }
        finally { process.StandardInput.Close(); }
    }

    private async Task ReadStderrAsync(Process process, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(process.StandardError.BaseStream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    writer.TryWrite(new AgentEvent.Status(line, AgentStatusKind.Stderr));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void HandleEvent(JsonElement root, ChannelWriter<AgentEvent> writer, HashSet<string> emitted, HashSet<string> started)
    {
        var type = String(root, "type");
        if (type == "thread.started")
        {
            if (String(root, "thread_id") is { Length: > 0 } threadId)
            {
                if (_sessionId is not null && _sessionId != threadId)
                    throw new InvalidOperationException("Codex resumed a different thread than requested.");
                _sessionId = threadId;
            }
            return;
        }
        if (type is "error" or "turn.failed")
        {
            var message = String(root, "message");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)) message ??= String(error, "message");
            writer.TryWrite(new AgentEvent.Error(message ?? "Codex turn failed."));
            return;
        }
        if (type is not ("item.started" or "item.updated" or "item.completed")
            || !root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return;
        var itemType = String(item, "type");
        var id = String(item, "id") ?? item.GetRawText();
        if (itemType == "agent_message")
        {
            if (type == "item.completed" && emitted.Add(id) && String(item, "text") is { Length: > 0 } text)
                writer.TryWrite(new AgentEvent.AssistantText(text));
            return;
        }
        if (itemType == "error")
        {
            if (type == "item.completed" && emitted.Add(id)) writer.TryWrite(new AgentEvent.Error(String(item, "message") ?? "Codex item failed."));
            return;
        }
        if (itemType is not ("command_execution" or "file_change" or "mcp_tool_call" or "web_search")) return;
        if (started.Add(id)) writer.TryWrite(new AgentEvent.ToolUse(itemType!, String(item, "command") ?? String(item, "tool") ?? String(item, "query") ?? item.GetRawText()));
        if (type == "item.completed" && emitted.Add(id))
        {
            var failed = String(item, "status") == "failed"
                || (item.TryGetProperty("exit_code", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out var code) && code != 0)
                || (item.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
            writer.TryWrite(new AgentEvent.ToolResult(id, failed));
        }
    }

    private static bool MatchesDirectory(string file, string? targetDirectory)
    {
        if (targetDirectory is null) return true;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (String(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var payload)) continue;
                var cwd = String(payload, "cwd");
                return cwd is null || string.Equals(Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
        return true;
    }

    private string? ResolveSessionFile()
    {
        var dir = Path.Combine(DefaultSessionRoot(), "sessions");
        if (!Directory.Exists(dir))
            return null;

        var files = Directory.GetFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories);
        if (_sessionId is { } id)
            return files.FirstOrDefault(f => f.Contains(id));

        return null;
    }
}
