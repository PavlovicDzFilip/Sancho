using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private Process? _process;
    private StreamWriter? _stdin;
    private TaskCompletionSource? _turnComplete;
    private volatile bool _ready;

    public ClaudeService(IOptions<ClaudeOptions> options)
    {
        var o = options.Value;
        _targetDirectory = string.IsNullOrWhiteSpace(o.TargetDirectory)
            ? Environment.CurrentDirectory
            : o.TargetDirectory;

        var promptPath = Path.IsPathRooted(o.PromptFilePath)
            ? o.PromptFilePath
            : Path.Combine(AppContext.BaseDirectory, o.PromptFilePath);

        if (!File.Exists(promptPath))
            throw new FileNotFoundException(
                $"System prompt file not found at '{promptPath}'. " +
                "Create a prompt.md file in the application directory, " +
                "or set Claude:PromptFilePath in appsettings.json.");

        _systemPrompt = File.ReadAllText(promptPath).Trim();
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

    public static void VerifyClaudeAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("claude", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(5_000);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"`claude --version` exited with code {proc.ExitCode}.");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not find `claude` CLI. " +
                "Make sure it is installed and on your PATH.\n" +
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
        var args = $"--print --verbose --input-format stream-json --output-format stream-json --permission-mode bypassPermissions --system-prompt \"{escapedPrompt}\"";

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
                $"claude process exited immediately with code {_process.ExitCode}.\n" +
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

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _turnComplete = tcs;

                var json = JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { role = "user", content = sentence }
                }, _jsonOptions);

                await _stdin!.WriteLineAsync(json);
                writer.TryWrite(ClaudeEvent.TurnStart.Instance);

                var timeout = Task.Delay(TimeSpan.FromMinutes(2), ct);
                var completed = await Task.WhenAny(tcs.Task, timeout);
                if (completed == timeout)
                {
                    writer.TryWrite(new ClaudeEvent.Status(
                        "⚠ Turn timed out", ClaudeStatusKind.Warning));
                }

                _turnComplete = null;
                _ready = true;
                writer.TryWrite(ClaudeEvent.Ready.Instance);
            }
        }
        catch (OperationCanceledException) { }
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
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
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
        catch (OperationCanceledException) { }
        catch (IOException) { }
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
        catch (OperationCanceledException) { }
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
                ? tid.GetString()?[..Math.Min(12, tid.GetString()!.Length)] : "?";
            var isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
            writer.TryWrite(new ClaudeEvent.ToolResult(toolId!, isError));
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
