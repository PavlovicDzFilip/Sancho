using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Agents;

/// <summary>
/// Agent backend for Cursor's CLI (<c>cursor-agent</c>). One process per
/// turn — cursor-agent documents a one-shot <c>-p</c> mode with stream-json
/// output but no long-lived stdin protocol — and turns are chained by
/// passing <c>--resume &lt;session_id&gt;</c>, where the session id is
/// captured from the first turn's init event.
/// </summary>
public sealed class CursorAgentService : AgentService
{
    private const string Executable = "cursor-agent";

    private readonly string _targetDirectory;
    private readonly string? _resumeSessionId;
    private readonly ILogger<CursorAgentService> _logger;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();

    private string? _sessionId;
    private volatile bool _ready;

    public CursorAgentService(string? resumeSessionId, ILogger<CursorAgentService> logger)
    {
        _targetDirectory = Directory.GetCurrentDirectory();
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

    /// <inheritdoc />
    public override IReadOnlyList<AgentService.SessionSummary> ListSessions(string targetDirectory) =>
        ListSessions(DefaultSessionRoot(), targetDirectory);

    /// <summary>The cursor session store: <c>~/.cursor</c>.</summary>
    public static string DefaultSessionRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor");

    /// <summary>
    /// Lists stored agent sessions for a target directory, newest first.
    /// Cursor keeps one <c>&lt;session-id&gt;.jsonl</c> transcript per
    /// <c>agent-transcripts/&lt;session-id&gt;/</c> directory.
    /// </summary>
    public static IReadOnlyList<AgentService.SessionSummary> ListSessions(
        string sessionRoot, string targetDirectory)
    {
        var dir = Path.Combine(sessionRoot, "projects",
            SessionJson.EncodeProjectDirectory(targetDirectory), "agent-transcripts");
        if (!Directory.Exists(dir))
            return Array.Empty<AgentService.SessionSummary>();

        return Directory.GetDirectories(dir)
            .Select(d =>
            {
                var id = Path.GetFileName(d);
                var jsonl = Path.Combine(d, id + ".jsonl");
                return new AgentService.SessionSummary(
                    id,
                    File.Exists(jsonl) ? File.GetLastWriteTime(jsonl) : File.GetLastWriteTime(d),
                    null,
                    File.Exists(jsonl)
                        ? SessionJson.GetSessionPreview(jsonl, SessionJson.Format.Cursor)
                        : "(no transcript)");
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
            : SessionJson.ReadMessages(file, SessionJson.Format.Cursor);
    }

    /// <inheritdoc />
    public override void VerifyAvailable() => VerifyAvailable(Executable);

    /// <summary>Checks cursor-agent is installed (auth failures surface on the first turn).</summary>
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
                    "cursor-agent is not installed. Install it from cursor.com and try again.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "Could not find `cursor-agent`. Install it from cursor.com and try again.");
        }
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
            WorkingDirectory = _targetDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(sentence);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        if (_sessionId is not null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(_sessionId);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {Executable} process.");
        _logger.LogDebug("→ {Executable}: {Text}", Executable, sentence);

        // stderr is drained concurrently so a chatty process can't deadlock
        // on a full pipe buffer.
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
            case "system":
                // init carries the session id — capture it for the next
                // turn's --resume so the conversation stays one session.
                if (_sessionId is null
                    && root.TryGetProperty("session_id", out var sid)
                    && sid.GetString() is { Length: > 0 } id)
                {
                    _sessionId = id;
                }

                break;

            case "assistant":
                EmitAssistantText(root, writer);
                break;

            case "tool_call":
                EmitToolCall(root, writer);
                break;

            case "result":
                writer.TryWrite(AgentEvent.TurnComplete.Instance);
                break;
        }
    }

    private static void EmitAssistantText(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("message", out var message))
            return;

        var text = SessionJson.ExtractText(message);
        if (!string.IsNullOrWhiteSpace(text))
            writer.TryWrite(new AgentEvent.AssistantText(text));
    }

    /// <summary>Cursor reports tools as <c>tool_call</c> events (started/completed).</summary>
    private static void EmitToolCall(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var subtype = root.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;
        if (subtype != "started")
            return;

        var name = "tool";
        if (root.TryGetProperty("tool_call", out var tc) && tc.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in tc.EnumerateObject())
            {
                name = prop.Name;
                break;
            }
        }

        writer.TryWrite(new AgentEvent.ToolUse(name, name));
    }

    private string? ResolveSessionFile()
    {
        var dir = Path.Combine(DefaultSessionRoot(), "projects",
            SessionJson.EncodeProjectDirectory(_targetDirectory), "agent-transcripts");
        if (!Directory.Exists(dir))
            return null;

        if (_resumeSessionId is { } id)
        {
            var path = Path.Combine(dir, id, id + ".jsonl");
            return File.Exists(path) ? path : null;
        }

        return Directory.GetDirectories(dir)
            .Select(d => Path.Combine(d, Path.GetFileName(d) + ".jsonl"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
