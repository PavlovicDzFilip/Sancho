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
/// turn's <c>session_meta</c> event carries the session id, which later
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
    private volatile bool _ready;

    public CodexAgentService(string? resumeSessionId, ILogger<CodexAgentService> logger)
    {
        _resumeSessionId = resumeSessionId;
        _sessionId = resumeSessionId;
        _logger = logger;
    }

    /// <inheritdoc />
    public override bool ContinueSession => _resumeSessionId is not null;

    /// <inheritdoc />
    public override void Send(string sentence)
    {
        if (!_ready)
            throw new InvalidOperationException($"{Executable} is not ready to accept input.");
        _input.Writer.TryWrite(sentence);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<AgentEvent> RunAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var events = Channel.CreateUnbounded<AgentEvent>();
        var worker = WorkerAsync(events.Writer, ct);

        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync())
                yield return evt;
        }
        finally
        {
            await worker;
        }
    }

    /// <summary>The codex session store: <c>~/.codex</c>.</summary>
    public static string DefaultSessionRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <inheritdoc />
    public override IReadOnlyList<AgentService.SessionSummary> ListSessions(string targetDirectory) =>
        ListAllSessions(DefaultSessionRoot());

    /// <summary>
    /// Lists stored sessions from the rollout files under
    /// <c>~/.codex/sessions/&lt;year&gt;/&lt;month&gt;/&lt;day&gt;/</c>,
    /// newest first. Codex sessions are global (not per directory), so the
    /// picker shows them all.
    /// </summary>
    public static IReadOnlyList<AgentService.SessionSummary> ListAllSessions(string sessionRoot)
    {
        var dir = Path.Combine(sessionRoot, "sessions");
        if (!Directory.Exists(dir))
            return Array.Empty<AgentService.SessionSummary>();

        return Directory.GetFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories)
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

    /// <summary>Checks codex is installed and has an auth.json (best-effort login signal).</summary>
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
            if (proc is null || !proc.WaitForExit(5_000) || proc.ExitCode != 0)
                throw new InvalidOperationException(
                    "codex is not installed. Install the Codex CLI from OpenAI and try again.");
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

        if (!File.Exists(Path.Combine(DefaultSessionRoot(), "auth.json")))
            throw new InvalidOperationException(
                "codex is not logged in. Run `codex login` and try again.");
    }

    // ── Turn worker ────────────────────────────────────────────────

    private async Task WorkerAsync(ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        try
        {
            _ready = true;
            writer.TryWrite(AgentEvent.Ready.Instance);

            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                _ready = false;
                writer.TryWrite(AgentEvent.TurnStart.Instance);
                await RunTurnAsync(writer, sentence, ct);
                writer.TryWrite(AgentEvent.TurnComplete.Instance);
                _ready = true;
                writer.TryWrite(AgentEvent.Ready.Instance);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            writer.TryWrite(new AgentEvent.Error($"{Executable} failed: {ex.Message}"));
        }
    }

    private async Task RunTurnAsync(ChannelWriter<AgentEvent> writer, string sentence, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("exec");
        if (_sessionId is not null)
        {
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add(_sessionId);
        }

        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add(sentence);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {Executable} process.");
        _logger.LogDebug("→ {Executable}: {Text}", Executable, sentence);

        var stderrTask = ReadStderrAsync(process, writer, ct);

        using var reader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            _logger.LogDebug("← {Executable}: {Line}", Executable, line);
            try
            {
                using var doc = JsonDocument.Parse(line);
                HandleEvent(doc.RootElement, writer);
            }
            catch (JsonException)
            {
                writer.TryWrite(new AgentEvent.Status(line, AgentStatusKind.Info));
            }
        }

        await process.WaitForExitAsync(ct);
        await stderrTask;

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
            writer.TryWrite(new AgentEvent.Error($"{Executable} exited with code {process.ExitCode}"));
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

    private void HandleEvent(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

        switch (type)
        {
            case "session_meta":
                if (_sessionId is null
                    && root.TryGetProperty("payload", out var meta)
                    && meta.TryGetProperty("id", out var id)
                    && id.GetString() is { Length: > 0 } sessionId)
                {
                    _sessionId = sessionId;
                }

                break;

            case "response_item":
                EmitResponseItem(root, writer);
                break;
        }
    }

    private static void EmitResponseItem(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("payload", out var payload))
            return;

        var itemType = payload.TryGetProperty("type", out var tp) ? tp.GetString() : null;
        switch (itemType)
        {
            case "message":
            {
                var role = payload.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (role == "assistant")
                {
                    var text = SessionJson.ExtractText(payload);
                    if (!string.IsNullOrWhiteSpace(text))
                        writer.TryWrite(new AgentEvent.AssistantText(text));
                }

                break;
            }

            case "function_call":
            {
                var name = payload.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                writer.TryWrite(new AgentEvent.ToolUse(name, name));
                break;
            }
        }
    }

    private string? ResolveSessionFile()
    {
        var dir = Path.Combine(DefaultSessionRoot(), "sessions");
        if (!Directory.Exists(dir))
            return null;

        var files = Directory.GetFiles(dir, "rollout-*.jsonl", SearchOption.AllDirectories);
        if (_resumeSessionId is { } id)
            return files.FirstOrDefault(f => f.Contains(id));

        return files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
