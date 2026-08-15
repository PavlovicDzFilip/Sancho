using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sancho.Console.Services;

/// <summary>
/// Thin wrapper around the Claude CLI. Call <see cref="RunAsync"/> to get
/// a stream of <see cref="ClaudeEvent"/>s, then call <see cref="Send"/> when
/// a <see cref="ClaudeEvent.Ready"/> event arrives.
/// </summary>
public sealed class ClaudeService
{
    private readonly string _targetDirectory;
    private readonly string _systemPrompt;
    private readonly string? _resumeSessionId;
    private readonly string _sessionId;
    private readonly ILogger<ClaudeService> _logger;
    private readonly SessionTitleService _titleService;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private Process? _process;
    private StreamWriter? _stdin;
    private TaskCompletionSource? _turnComplete;
    private volatile bool _ready;

    // First user message, captured for session title generation.
    private string? _firstUserText;
    private int _titleRequested;

    public ClaudeService(
        IOptions<ClaudeOptions> options,
        string? resumeSessionId,
        ILogger<ClaudeService> logger,
        SessionTitleService titleService)
    {
        var o = options.Value;
        _targetDirectory = o.TargetDirectory;

        var promptPath = Path.IsPathRooted(o.PromptFilePath)
            ? o.PromptFilePath
            : Path.Combine(AppContext.BaseDirectory, o.PromptFilePath);

        if (!File.Exists(promptPath))
            throw new FileNotFoundException(
                $"System prompt file not found at '{promptPath}'. " +
                "Create a prompt.md file in the application directory, " +
                "or set Claude:PromptFilePath in appsettings.json.");

        _systemPrompt = File.ReadAllText(promptPath).Trim();
        _resumeSessionId = resumeSessionId;
        _logger = logger;
        _titleService = titleService;
        _sessionId = resumeSessionId ?? Guid.NewGuid().ToString("D");
    }

    // ── Public API ─────────────────────────────────────────────────

    /// <summary>
    /// Send a sentence to Claude. Throws if the service is not in a
    /// <see cref="ClaudeEvent.Ready"/> state.
    /// </summary>
    public void Send(string sentence)
    {
        if (!_ready)
            throw new InvalidOperationException("Claude is not ready to accept input.");
        _input.Writer.TryWrite(sentence);
    }

    /// <summary>Whether a previous conversation is being resumed on startup.</summary>
    public bool ContinueSession => _resumeSessionId is not null;

    /// <summary>Summary of a stored session, used by the <c>--continue</c> chooser.</summary>
    /// <param name="Title">User-facing session name, when one was saved; <c>null</c> to fall back to <see cref="Preview"/>.</param>
    public sealed record SessionSummary(string Id, DateTime LastActivity, string? Title, string Preview);

    /// <summary>Resolves the configured target directory to an absolute path.</summary>
    public static string ResolveTargetDirectory(string? configured) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Environment.CurrentDirectory : configured);

    /// <summary>Lists stored sessions for a target directory, newest first.</summary>
    public static IReadOnlyList<SessionSummary> ListSessions(string targetDirectory)
    {
        var dir = GetSessionsDirectory(targetDirectory);
        if (!Directory.Exists(dir))
            return Array.Empty<SessionSummary>();

        return Directory.GetFiles(dir, "*.jsonl")
            .Select(file =>
            {
                var id = Path.GetFileNameWithoutExtension(file);
                return new SessionSummary(
                    id,
                    File.GetLastWriteTime(file),
                    GetSessionTitle(targetDirectory, id),
                    GetSessionPreview(file));
            })
            .OrderByDescending(s => s.LastActivity)
            .ToList();
    }

    /// <summary>
    /// Reads all user/assistant text messages from the resumed (or most
    /// recent) Claude session, in chronological order.
    /// </summary>
    public IReadOnlyList<(bool IsUser, string Text)> GetSessionMessages()
    {
        var file = ResolveSessionFile();
        return file is null ? Array.Empty<(bool IsUser, string Text)>() : ReadMessages(file);
    }

    private string? ResolveSessionFile()
    {
        var dir = GetSessionsDirectory(_targetDirectory);
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

    private static string GetSessionsDirectory(string targetDirectory) =>
        Path.Combine(GetClaudeConfigDir(), "projects", EncodeProjectDirectory(targetDirectory));

    private string GetTitlePath(string sessionId) =>
        Path.Combine(GetSessionsDirectory(_targetDirectory), sessionId + ".title");

    /// <summary>Reads the saved display name for a session, if any.</summary>
    private static string? GetSessionTitle(string targetDirectory, string id)
    {
        try
        {
            var path = Path.Combine(GetSessionsDirectory(targetDirectory), id + ".title");
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

    private static IReadOnlyList<(bool IsUser, string Text)> ReadMessages(string file)
    {
        var messages = new List<(bool IsUser, string Text)>();
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() is { } type
                    && type is "user" or "assistant"
                    && root.TryGetProperty("message", out var message))
                {
                    var role = message.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null;
                    if (role is not ("user" or "assistant"))
                        continue;

                    var text = ExtractText(message);
                    if (!string.IsNullOrWhiteSpace(text))
                        messages.Add((role == "user", text));
                }
            }
            catch (JsonException)
            {
                // Malformed line — skip it.
            }
        }

        return messages;
    }

    private static string GetSessionPreview(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() is { } type
                    && type is "user" or "assistant"
                    && root.TryGetProperty("message", out var message))
                {
                    var text = ExtractText(message);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Substring(0, Math.Min(120, text.Length));
                }
            }
            catch (JsonException)
            {
                // Malformed line — skip it.
            }
        }

        return "(no messages)";
    }

    private static string GetClaudeConfigDir()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    private static string EncodeProjectDirectory(string path)
    {
        var sb = new StringBuilder();
        foreach (var c in path)
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    private static string ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                && block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(t.GetString());
            }
        }

        return sb.ToString();
    }

    public static void VerifyClaudeAvailable()
    {
        var versionExit = RunClaudeCommand("--version");
        if (versionExit != 0)
            throw new InvalidOperationException(
                $"`claude --version` exited with code {versionExit}.");

        if (RunClaudeCommand("auth status") != 0)
            throw new InvalidOperationException(
                "Claude CLI is not logged in. Run `claude auth login` and try again.");
    }

    private static int RunClaudeCommand(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("claude", args)
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
                "Could not find `claude` CLI. " +
                "Make sure it is installed and on your PATH. " +
                Environment.NewLine +
                $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Start the Claude process and return a stream of events.
    /// Enumeration is lazy — the process starts on first MoveNext.
    /// </summary>
    public async IAsyncEnumerable<ClaudeEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ── Spawn process ────────────────────────────────────────
        var escapedPrompt = _systemPrompt.Replace("\"", "\\\"");
        var resumeFlag = _resumeSessionId is { } id
            ? $" --resume \"{id}\""
            : $" --session-id \"{_sessionId}\"";
        var args =
            $"--print --verbose --input-format stream-json --output-format stream-json --permission-mode bypassPermissions{resumeFlag} --system-prompt \"{escapedPrompt}\"";

        var psi = new ProcessStartInfo("claude", args)
        {
            WorkingDirectory = _targetDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi)
                   ?? throw new InvalidOperationException("Failed to start claude process.");

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
                $"claude process exited immediately with code {_process.ExitCode}." +
                Environment.NewLine +
                $"stderr: {errText.Trim()}");
        }

        // ── Event channel — bridges background tasks → enumerable ─
        var events = Channel.CreateUnbounded<ClaudeEvent>();

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

    private async Task ProcessInputAsync(ChannelWriter<ClaudeEvent> writer, CancellationToken ct)
    {
        try
        {
            _ready = true;
            writer.TryWrite(ClaudeEvent.Ready.Instance);

            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                _ready = false;

                if (_firstUserText is null)
                {
                    _firstUserText = sentence.Trim();
                    RequestTitleGeneration();
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _turnComplete = tcs;

                var json = JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { role = "user", content = sentence }
                }, _jsonOptions);

                await _stdin!.WriteLineAsync(json);
                writer.TryWrite(ClaudeEvent.TurnStart.Instance);

                // No turn timeout — wait until Claude finishes. The watchdog
                // completes this on process exit; cancellation aborts the wait.
                await tcs.Task.WaitAsync(ct);

                _turnComplete = null;
                _ready = true;
                writer.TryWrite(ClaudeEvent.Ready.Instance);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            writer.TryWrite(new ClaudeEvent.Error(
                $"Claude process connection lost: {ex.Message}"));
        }
    }

    // ── Stdout reader ──────────────────────────────────────────────

    private async Task ReadStdoutAsync(
        Process process, ChannelWriter<ClaudeEvent> writer, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    HandleStreamMessage(doc.RootElement, writer);
                }
                catch (JsonException)
                {
                    writer.TryWrite(new ClaudeEvent.Status(line, ClaudeStatusKind.Info));
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
        Process process, ChannelWriter<ClaudeEvent> writer, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(process.StandardError.BaseStream, Encoding.UTF8);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    writer.TryWrite(new ClaudeEvent.Status(line, ClaudeStatusKind.Stderr));
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
        Process process, ChannelWriter<ClaudeEvent> writer, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
            writer.TryWrite(new ClaudeEvent.Error(
                $"Claude process exited unexpectedly (code {process.ExitCode})"));
            _turnComplete?.TrySetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Stream-json message dispatcher ─────────────────────────────

    private void HandleStreamMessage(JsonElement root, ChannelWriter<ClaudeEvent> writer)
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
                writer.TryWrite(ClaudeEvent.TurnComplete.Instance);
                _turnComplete?.TrySetResult();
                break;
        }
    }

    private static void HandleAssistantMessage(JsonElement root, ChannelWriter<ClaudeEvent> writer)
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
                            writer.TryWrite(new ClaudeEvent.AssistantText(text));
                        break;
                    }

                    case "tool_use":
                    {
                        var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : "?";
                        var preview = FormatToolPreview(toolName!,
                            block.TryGetProperty("input", out var ti) ? ti : default);
                        writer.TryWrite(new ClaudeEvent.ToolUse(toolName!, preview));
                        break;
                    }
                }
            }
        }
    }

    private static void HandleUserMessage(JsonElement root, ChannelWriter<ClaudeEvent> writer)
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
            writer.TryWrite(new ClaudeEvent.ToolResult(toolId!, isError));
        }
    }

    // ── Session title generation ───────────────────────────────────

    /// <summary>
    /// Kicks off one-time background title generation as soon as the first
    /// message is sent, unless the session already has a name.
    /// </summary>
    private void RequestTitleGeneration()
    {
        if (Interlocked.Exchange(ref _titleRequested, 1) != 0)
            return;
        if (File.Exists(GetTitlePath(_sessionId)))
            return;

        _ = Task.Run(GenerateAndSaveTitleAsync);
    }

    private async Task GenerateAndSaveTitleAsync()
    {
        try
        {
            if (File.Exists(GetTitlePath(_sessionId)))
                return;

            var title = await _titleService.GenerateTitleAsync(_firstUserText);
            if (title is null)
                return;

            Directory.CreateDirectory(GetSessionsDirectory(_targetDirectory));
            File.WriteAllText(GetTitlePath(_sessionId), title);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Session title generation failed: {Message}", ex.Message);
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