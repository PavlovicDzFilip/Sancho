using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sancho.Console;

public sealed class ClaudeService
{
    private const string Orange = "[38;5;214m";
    private const string Dim    = "[38;5;240m";
    private const string Yellow = "[38;5;220m";
    private const string Reset  = "[0m";

    private readonly string _targetDirectory;
    private readonly string _systemPrompt;
    private readonly ILogger<ClaudeService> _logger;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private Process? _process;
    private StreamWriter? _stdin;
    private TaskCompletionSource? _turnComplete;
    private readonly StringBuilder _turnBuffer = new();
    private bool _turnHasContent;

    public ClaudeService(IOptions<ClaudeOptions> options, ILogger<ClaudeService> logger)
    {
        var o = options.Value;
        _targetDirectory = string.IsNullOrWhiteSpace(o.TargetDirectory)
            ? Environment.CurrentDirectory
            : o.TargetDirectory;
        _systemPrompt = o.SystemPrompt;
        _logger = logger;
    }

    // ── Public API ─────────────────────────────────────────────────

    public bool Enqueue(string sentence)
    {
        return _input.Writer.TryWrite(sentence);
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

    public async Task RunAsync(CancellationToken ct)
    {
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

        // Startup health check
        await Task.Delay(1500, ct);
        if (_process.HasExited)
        {
            var errText = await _process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"claude process exited immediately with code {_process.ExitCode}.\n" +
                $"stderr: {errText.Trim()}");
        }

        _logger.LogInformation("{Dim}🤖 Claude assistant ready ({Dir}){Reset}", Dim, _targetDirectory, Reset);

        var stdoutTask = ReadStdoutAsync(_process, ct);
        var stderrTask = LogStderrAsync(ct);
        _ = WatchProcessExitAsync(_process, ct);

        // ── Main loop ───────────────────────────────────────────
        try
        {
            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                _turnBuffer.Clear();
                _turnHasContent = false;
                _turnComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var json = JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { role = "user", content = sentence }
                }, _jsonOptions);

                await _stdin.WriteLineAsync(json);

                var timeout = Task.Delay(TimeSpan.FromMinutes(2), ct);
                var completed = await Task.WhenAny(_turnComplete.Task, timeout);
                if (completed == timeout)
                {
                    _logger.LogWarning("{Yellow}⚠ Turn timed out{Reset}", Yellow, Reset);
                }

                _turnComplete = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            _logger.LogError(ex, "{Yellow}⚠ Claude process connection lost{Reset}", Yellow, Reset);
        }
        finally
        {
            try { _stdin.Close(); } catch { }
        }

        try
        {
            if (!_process.HasExited)
            {
                await Task.WhenAny(stdoutTask, Task.Delay(3_000, ct));
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        await Task.WhenAll(stdoutTask, stderrTask);
    }

    // ── Stdout reader ──────────────────────────────────────────────

    private async Task ReadStdoutAsync(Process process, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    HandleStreamMessage(doc.RootElement);
                }
                catch (JsonException)
                {
                    _logger.LogDebug("{Dim}{Line}{Reset}", Dim, line, Reset);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    // ── Stderr reader ──────────────────────────────────────────────

    private Task LogStderrAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(_process!.StandardError.BaseStream, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger.LogWarning("{Dim}[claude] {Line}{Reset}", Dim, line, Reset);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }, ct);
    }

    // ── Process watchdog ───────────────────────────────────────────

    private async Task WatchProcessExitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
            _logger.LogWarning("{Yellow}⚠ Claude process exited unexpectedly (code {Code}){Reset}",
                Yellow, process.ExitCode, Reset);
            _turnComplete?.TrySetResult();
        }
        catch (OperationCanceledException) { }
    }

    // ── Stream-json message dispatcher ─────────────────────────────

    private void HandleStreamMessage(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

        switch (type)
        {
            case "system":
                break;

            case "assistant":
                HandleAssistantMessage(root);
                break;

            case "user":
                HandleUserMessage(root);
                break;

            case "result":
                FlushTurn();
                _turnComplete?.TrySetResult();
                break;
        }
    }

    private void HandleAssistantMessage(JsonElement root)
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
                            _turnBuffer.Append(text);
                        break;
                    }

                    case "tool_use":
                    {
                        _turnHasContent = true;
                        var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : "?";
                        var preview = FormatToolPreview(toolName!,
                            block.TryGetProperty("input", out var ti) ? ti : default);
                        _turnBuffer.AppendLine().Append($"{Dim}[{toolName}: {preview}]{Orange}");
                        break;
                    }
                }
            }
        }
    }

    private void HandleUserMessage(JsonElement root)
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
            _turnBuffer.AppendLine()
                      .Append($"{Dim}[tool {toolId}… {(isError ? "✗" : "✓")}]{Orange}");
        }
    }

    /// <summary>
    /// At end of turn: check the response content and render in the appropriate mode.
    /// </summary>
    private void FlushTurn()
    {
        var raw = _turnBuffer.ToString().Trim();
        string? output = null;

        if (_turnHasContent)
        {
            output = raw.Length > 0
                ? $"{Orange}{_turnBuffer}{Reset}\n"
                : "\n";
        }
        else if (raw is "…" or "...")
        {
            _logger.LogDebug("{Dim}🤖 listening…{Reset}", Dim, Reset);
        }
        else if (raw.StartsWith("\U0001F4A1"))
        {
            output = $"{Dim}{raw}{Reset}";
        }
        else if (raw.Length > 0)
        {
            output = $"{Orange}{raw}{Reset}";
        }

        if (output is not null)
            _logger.LogInformation("{Text}", output);
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
