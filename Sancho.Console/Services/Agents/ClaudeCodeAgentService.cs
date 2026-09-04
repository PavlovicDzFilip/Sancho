using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Agents;

/// <summary>
/// Agent backend for Claude-Code-protocol CLIs (the Claude CLI, and Cursor's
/// CLI which reimplements the same stream-json interface). Call
/// <see cref="RunAsync"/> to get a stream of <see cref="AgentEvent"/>s, then
/// call <see cref="Send"/> when an <see cref="AgentEvent.Ready"/> event
/// arrives.
/// </summary>
public sealed class ClaudeCodeAgentService : AgentService
{
    private readonly string _executable;
    private readonly string _sessionRoot;
    private readonly string _targetDirectory;
    private readonly string _systemPrompt;
    private readonly string? _resumeSessionId;
    private readonly string _sessionId;
    private readonly ILogger<ClaudeCodeAgentService> _logger;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();

    private Process? _process;
    private StreamWriter? _stdin;
    private TaskCompletionSource? _turnComplete;
    private volatile bool _ready;

    public ClaudeCodeAgentService(
        string executable,
        string? sessionRoot,
        string? resumeSessionId,
        ILogger<ClaudeCodeAgentService> logger)
    {
        _executable = executable;
        _sessionRoot = sessionRoot ?? DefaultSessionRoot();
        _targetDirectory = Directory.GetCurrentDirectory();
        _logger = logger;

        var promptPath = EnsureSystemPrompt(_targetDirectory).Path;
        _systemPrompt = File.ReadAllText(promptPath).Trim();
        _resumeSessionId = resumeSessionId;
        _sessionId = resumeSessionId ?? Guid.NewGuid().ToString("D");
    }

    // ── Public API ─────────────────────────────────────────────────

    /// <summary>
    /// Send a sentence to the agent. Throws if the agent is not in a
    /// <see cref="AgentEvent.Ready"/> state.
    /// </summary>
    public override void Send(string sentence)
    {
        if (!_ready)
            throw new InvalidOperationException($"{_executable} is not ready to accept input.");
        _input.Writer.TryWrite(sentence);
    }

    /// <inheritdoc />
    public override bool ContinueSession => _resumeSessionId is not null;

    /// <summary>Lists stored sessions for a target directory, newest first.</summary>
    public static IReadOnlyList<AgentService.SessionSummary> ListSessions(
        string sessionRoot, string targetDirectory)
    {
        var dir = GetSessionsDirectory(sessionRoot, targetDirectory);
        if (!Directory.Exists(dir))
            return Array.Empty<AgentService.SessionSummary>();

        return Directory.GetFiles(dir, "*.jsonl")
            .Select(file =>
            {
                var id = Path.GetFileNameWithoutExtension(file);
                return new AgentService.SessionSummary(
                    id,
                    File.GetLastWriteTime(file),
                    GetSessionTitle(sessionRoot, targetDirectory, id),
                    SessionJson.GetSessionPreview(file, SessionJson.Format.ClaudeCode));
            })
            .OrderByDescending(s => s.LastActivity)
            .ToList();
    }

    /// <inheritdoc />
    public override IReadOnlyList<AgentService.SessionSummary> ListSessions(string targetDirectory) =>
        ListSessions(_sessionRoot, targetDirectory);

    /// <summary>
    /// Reads all user/assistant text messages from the resumed (or most
    /// recent) session, in chronological order.
    /// </summary>
    public override IReadOnlyList<(bool IsUser, string Text)> GetSessionMessages()
    {
        var file = ResolveSessionFile();
        return file is null
            ? Array.Empty<(bool IsUser, string Text)>()
            : SessionJson.ReadMessages(file, SessionJson.Format.ClaudeCode);
    }

    private string? ResolveSessionFile()
    {
        var dir = GetSessionsDirectory(_sessionRoot, _targetDirectory);
        if (!Directory.Exists(dir))
            return null;

        if (_resumeSessionId is { } id)
        {
            var path = Path.Combine(dir, id + ".jsonl");
            return File.Exists(path) ? path : null;
        }

        return Directory.GetFiles(dir, "*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string GetSessionsDirectory(string sessionRoot, string targetDirectory) =>
        Path.Combine(sessionRoot, "projects", SessionJson.EncodeProjectDirectory(targetDirectory));

    /// <summary>Reads the saved display name for a session, if any.</summary>
    private static string? GetSessionTitle(string sessionRoot, string targetDirectory, string id)
    {
        try
        {
            var path = Path.Combine(GetSessionsDirectory(sessionRoot, targetDirectory), id + ".title");
            if (!File.Exists(path))
                return null;

            var title = File.ReadAllText(path).Trim();
            return title.Length == 0 ? null : title;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The claude-family session store: <c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>.</summary>
    public static string DefaultSessionRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    /// <summary>Default prompt written to <c>.sancho.md</c> when it is missing.</summary>
    public const string DefaultSystemPrompt =
        """
        # Sancho Assistant

        You are a live assistant listening to someone speak. You receive transcribed sentences in realtime as they become available.

        ## Guidelines

        - **Be brief.** Respond only when action is needed or a question is asked. Don't respond to every sentence.
        - **Acknowledge.** Let the speaker know you heard them, but keep responses short and helpful.
        - **Execute commands.** When asked to run a command, use the Bash tool and report results clearly.

        """;

    /// <summary>
    /// Returns the <c>.sancho.md</c> path for the given directory, creating the
    /// file with <see cref="DefaultSystemPrompt"/> when it is missing. The
    /// <c>Created</c> flag lets callers tell the user the file is new.
    /// </summary>
    public static (string Path, bool Created) EnsureSystemPrompt(string targetDirectory)
    {
        var promptPath = Path.Combine(targetDirectory, ".sancho.md");

        if (File.Exists(promptPath))
            return (promptPath, false);

        File.WriteAllText(promptPath, DefaultSystemPrompt);
        return (promptPath, true);
    }

    /// <inheritdoc />
    public override void VerifyAvailable() => VerifyAvailable(_executable);

    /// <summary>Checks a Claude-Code-protocol CLI is installed and authenticated.</summary>
    public static void VerifyAvailable(string executable)
    {
        var versionExit = RunCommand(executable, "--version");
        if (versionExit != 0)
            throw new InvalidOperationException(
                $"`{executable} --version` exited with code {versionExit}.");

        if (RunCommand(executable, "auth status") != 0)
            throw new InvalidOperationException(
                $"{executable} is not logged in. Run `{executable} auth login` and try again.");
    }

    private static int RunCommand(string executable, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(5_000);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not find `{executable}` CLI. " +
                "Make sure it is installed and on your PATH. " +
                Environment.NewLine +
                $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Start the agent process and return a stream of events.
    /// Enumeration is lazy — the process starts on first MoveNext.
    /// </summary>
    public override async IAsyncEnumerable<AgentEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ── Spawn process ────────────────────────────────────────
        var escapedPrompt = _systemPrompt.Replace("\"", "\\\"");
        var resumeFlag = _resumeSessionId is { } id
            ? $" --resume \"{id}\""
            : $" --session-id \"{_sessionId}\"";
        var args =
            $"--print --verbose --input-format stream-json --output-format stream-json --permission-mode bypassPermissions{resumeFlag} --system-prompt \"{escapedPrompt}\"";

        var psi = new ProcessStartInfo(_executable, args)
        {
            WorkingDirectory = _targetDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi)
                   ?? throw new InvalidOperationException($"Failed to start {_executable} process.");

        _stdin = new StreamWriter(_process.StandardInput.BaseStream, Encoding.UTF8)
        {
            AutoFlush = true
        };

        // Health check
        await Task.Delay(1500, ct);
        if (_process.HasExited)
        {
            var errText = await _process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"{_executable} process exited immediately with code {_process.ExitCode}." +
                Environment.NewLine +
                $"stderr: {errText.Trim()}");
        }

        // ── Event channel — bridges background tasks → enumerable ─
        var events = Channel.CreateUnbounded<AgentEvent>();

        // Complete the channel when cancelled so ReadAllAsync exits cleanly
        await using var reg = ct.Register(() => events.Writer.TryComplete());

        var stdoutTask = ReadStdoutAsync(_process, events.Writer, ct);
        var stderrTask = ReadStderrAsync(_process, events.Writer, ct);
        var watchdogTask = WatchProcessAsync(_process, events.Writer, ct);
        var inputTask = ProcessInputAsync(events.Writer, ct);

        // Yield events as they arrive
        await foreach (var evt in events.Reader.ReadAllAsync(ct))
            yield return evt;

        // ── Cleanup ──────────────────────────────────────────────
        try
        {
            _stdin.Close();
        }
        catch
        {
            // Nothing to do
        }

        if (!_process.HasExited)
        {
            await Task.WhenAny(
                Task.WhenAll(stdoutTask, stderrTask, watchdogTask, inputTask),
                Task.Delay(3_000, ct));

            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        await Task.WhenAll(stdoutTask, stderrTask, watchdogTask, inputTask);
    }

    // ── Input processor ────────────────────────────────────────────

    private async Task ProcessInputAsync(ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        try
        {
            _ready = true;
            writer.TryWrite(AgentEvent.Ready.Instance);

            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                _ready = false;

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _turnComplete = tcs;

                var json = new JsonObject
                {
                    ["type"] = "user",
                    ["message"] = new JsonObject { ["role"] = "user", ["content"] = sentence }
                }.ToJsonString();

                _logger.LogDebug("→ {Executable}: {Text}", _executable, sentence);
                await _stdin!.WriteLineAsync(json);
                writer.TryWrite(AgentEvent.TurnStart.Instance);

                // No turn timeout — wait until Claude finishes. The watchdog
                // completes this on process exit; cancellation aborts the wait.
                await tcs.Task.WaitAsync(ct);

                _turnComplete = null;
                _ready = true;
                writer.TryWrite(AgentEvent.Ready.Instance);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            writer.TryWrite(new AgentEvent.Error(
                $"{_executable} process connection lost: {ex.Message}"));
        }
    }

    // ── Stdout reader ──────────────────────────────────────────────

    private async Task ReadStdoutAsync(
        Process process, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                _logger.LogDebug("← {Executable}: {Line}", _executable, line);

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    HandleStreamMessage(doc.RootElement, writer);
                }
                catch (JsonException)
                {
                    writer.TryWrite(new AgentEvent.Status(line, AgentStatusKind.Info));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    // ── Stderr reader ──────────────────────────────────────────────

    private async Task ReadStderrAsync(
        Process process, ChannelWriter<AgentEvent> writer, CancellationToken ct)
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

    // ── Process watchdog ───────────────────────────────────────────

    private async Task WatchProcessAsync(
        Process process, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
            writer.TryWrite(new AgentEvent.Error(
                $"{_executable} process exited unexpectedly (code {process.ExitCode})"));
            _turnComplete?.TrySetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Stream-json message dispatcher ─────────────────────────────

    private void HandleStreamMessage(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

        switch (type)
        {
            case "system":
                break;

            case "assistant":
                HandleAssistantMessage(root, writer);
                break;

            case "user":
                HandleUserMessage(root, writer);
                break;

            case "result":
                writer.TryWrite(AgentEvent.TurnComplete.Instance);
                _turnComplete?.TrySetResult();
                break;
        }
    }

    private static void HandleAssistantMessage(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("message", out var msg))
            return;

        if (msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;

                switch (blockType)
                {
                    case "text":
                    {
                        var text = block.TryGetProperty("text", out var t) ? t.GetString() : null;
                        if (!string.IsNullOrEmpty(text))
                            writer.TryWrite(new AgentEvent.AssistantText(text));
                        break;
                    }

                    case "tool_use":
                    {
                        var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : "?";
                        var preview = FormatToolPreview(toolName!,
                            block.TryGetProperty("input", out var ti) ? ti : default);
                        writer.TryWrite(new AgentEvent.ToolUse(toolName!, preview));
                        break;
                    }
                }
            }
        }
    }

    private static void HandleUserMessage(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("message", out var msg))
            return;
        if (!msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (blockType != "tool_result")
                continue;

            var toolId = block.TryGetProperty("tool_use_id", out var tid)
                ? tid.GetString()?[..Math.Min(12, tid.GetString()!.Length)]
                : "?";
            var isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
            writer.TryWrite(new AgentEvent.ToolResult(toolId!, isError));
        }
    }

    private static string FormatToolPreview(string name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return name;

        if (input.TryGetProperty("command", out var cmd))
        {
            var c = cmd.GetString() ?? "";
            return c.Length > 80 ? c[..80] + "…" : c;
        }

        foreach (var prop in input.EnumerateObject())
        {
            if (prop.Name is "description" or "message" or "query" or "path" or "file_path")
            {
                var v = prop.Value.GetString() ?? "";
                return v.Length > 80 ? v[..80] + "…" : v;
            }
        }

        return input.GetRawText().Length > 80
            ? input.GetRawText()[..80] + "…"
            : input.GetRawText();
    }
}