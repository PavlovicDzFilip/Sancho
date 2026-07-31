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
                            _turnBuffer.Append($"{Orange}{text}{Reset}");
                        break;
                    }

                    case "tool_use":
                    {
                        _turnHasContent = true;
                        var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : "?";
                        var preview = FormatToolPreview(toolName!,
                            block.TryGetProperty("input", out var ti) ? ti : default);
                        _turnBuffer.Append($"{Dim}[{toolName}: {preview}]{Reset}");
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
                      .Append($"{Dim}[tool {toolId}… {(isError ? "✗" : "✓")}]{Reset}");
        }
    }

    /// <summary>
    /// At end of turn: if Claude only responded with "…" and used no tools,
    /// suppress the output. Otherwise log the buffered response.
    /// </summary>
    private void FlushTurn()
    {
        var raw = StripAnsi(_turnBuffer).Trim();

        if (_turnHasContent)
        {
            // Real work was done — always show
            if (raw.Length > 0)
                _logger.LogInformation("{Text}", _turnBuffer.ToString());
        }
        else if (raw is "…" or "...")
        {
            // Listening acknowledgement — suppress
            _logger.LogDebug("{Dim}🤖 listening…{Reset}", Dim, Reset);
        }
        else if (raw.Length > 0)
        {
            // Real text response, no tools
            _turnBuffer.AppendLine();
            _logger.LogInformation("{Text}", _turnBuffer.ToString());
        }
    }

    private static string StripAnsi(StringBuilder sb)
    {
        var result = new StringBuilder(sb.Length);
        var inside = false;
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '')
            {
                inside = true;
                continue;
            }
            if (inside)
            {
                if (sb[i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                    inside = false;
                continue;
            }
            result.Append(sb[i]);
        }
        return result.ToString();
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
