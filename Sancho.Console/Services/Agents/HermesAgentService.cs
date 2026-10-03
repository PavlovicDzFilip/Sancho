using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Agents;

/// <summary>Hermes structured one-shot turns, continued using the directly reported session ID.</summary>
public sealed class HermesAgentService : AgentService
{
    private const string Executable = "hermes";
    private static readonly Regex SessionIdPattern = new(@"\b\d{8}_\d{6}_[0-9a-fA-F]+\b", RegexOptions.Compiled);
    private readonly string? _resumeSessionId;
    private readonly ILogger<HermesAgentService> _logger;
    private readonly AgentLaunchOptions _launchOptions;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
    private string? _sessionId;
    private int _ready;

    public HermesAgentService(string? resumeSessionId, ILogger<HermesAgentService> logger, AgentLaunchOptions? launchOptions = null)
    {
        _resumeSessionId = resumeSessionId;
        _sessionId = resumeSessionId;
        _logger = logger;
        _launchOptions = launchOptions ?? new AgentLaunchOptions();
    }

    public override bool ContinueSession => _resumeSessionId is not null;
    public override void Send(string sentence)
    {
        if (Interlocked.CompareExchange(ref _ready, 0, 1) != 1)
            throw new InvalidOperationException("hermes is not ready to accept input.");
        if (!_input.Writer.TryWrite(sentence))
            throw new InvalidOperationException("Agent event stream has stopped.");
    }

    public override async IAsyncEnumerable<AgentEvent> RunAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var events = Channel.CreateUnbounded<AgentEvent>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var worker = WorkerAsync(events.Writer, lifetime.Token);
        try { await foreach (var evt in events.Reader.ReadAllAsync()) yield return evt; }
        finally { lifetime.Cancel(); await worker; }
    }

    public override IReadOnlyList<SessionSummary> ListSessions(string targetDirectory) =>
        ParseSessionList(Capture(_launchOptions, ["sessions", "list", "--workspace", Path.GetFullPath(targetDirectory)]).Output);

    public static IReadOnlyList<SessionSummary> ListSessions() =>
        ParseSessionList(Capture(new AgentLaunchOptions(), ["sessions", "list"]).Output);

    internal static IReadOnlyList<SessionSummary> ParseSessionList(string output)
    {
        var sessions = new List<SessionSummary>();
        foreach (var line in output.Split('\n'))
        {
            var match = SessionIdPattern.Match(line);
            if (!match.Success) continue;
            var preview = line.Replace(match.Value, "").Trim(' ', '|', '-', '\t', '\r');
            sessions.Add(new SessionSummary(match.Value, DateTime.MinValue, null, preview.Length > 0 ? preview : "(session)"));
        }
        return sessions;
    }

    public override IReadOnlyList<(bool IsUser, string Text)> GetSessionMessages()
    {
        if (_sessionId is null) return Array.Empty<(bool, string)>();
        var export = Path.Combine(Path.GetTempPath(), $"sancho-hermes-{Guid.NewGuid():N}.jsonl");
        try
        {
            var result = Capture(_launchOptions, ["sessions", "export", export, "--session-id", _sessionId]);
            return result.ExitCode == 0 && File.Exists(export) ? ReadExport(export) : Array.Empty<(bool, string)>();
        }
        finally { try { File.Delete(export); } catch (IOException) { } }
    }

    internal static IReadOnlyList<(bool IsUser, string Text)> ReadExport(string path)
    {
        var result = new List<(bool, string)>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.Object) continue;
                    var role = String(message, "role");
                    if (role is not ("user" or "assistant")) continue;
                    var text = SessionJson.ExtractText(message);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add((role == "user", text));
                }
            }
            catch (JsonException) { }
        }
        return result;
    }

    public override void VerifyAvailable() => VerifyAvailable(_launchOptions.Executable ?? Executable);
    public static void VerifyAvailable(string executable = Executable)
    {
        if (Capture(new AgentLaunchOptions { Executable = executable }, ["--version"]).ExitCode != 0)
            throw new InvalidOperationException("Could not run `hermes`. Install it from hermes.dev and try again.");
    }

    private async Task WorkerAsync(ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        var active = false;
        try
        {
            Interlocked.Exchange(ref _ready, 1);
            writer.TryWrite(AgentEvent.Ready.Instance);
            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                active = true;
                writer.TryWrite(AgentEvent.TurnStart.Instance);
                await RunTurnAsync(sentence, writer, ct);
                writer.TryWrite(AgentEvent.TurnComplete.Instance);
                active = false;
                Interlocked.Exchange(ref _ready, 1);
                writer.TryWrite(AgentEvent.Ready.Instance);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            writer.TryWrite(new AgentEvent.Error($"hermes failed: {ex.Message}"));
            if (active) writer.TryWrite(AgentEvent.TurnComplete.Instance);
        }
        finally
        {
            Interlocked.Exchange(ref _ready, 0);
            _input.Writer.TryComplete();
            writer.TryComplete();
        }
    }

    private async Task RunTurnAsync(string sentence, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        var instructions = _launchOptions.ReadInstructions();
        var prompt = instructions.Length == 0 ? sentence : $"<sancho_instructions>\n{instructions}\n</sancho_instructions>\n\n{sentence}";
        var args = new List<string> { "--in", Path.GetFullPath(_launchOptions.WorkingDirectory), "chat", "-q", prompt, "--format", "stream-json", "--oneshot", "--yolo" };
        if (_sessionId is not null) args.AddRange(["--resume", _sessionId]);
        await using var owned = OwnedAgentProcess.Start(_launchOptions.CreateStartInfo(Executable, args), ct);
        var process = owned.Process;
        process.StandardInput.Close();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var terminal = false;
        var emittedText = false;
        string? failure = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // Hermes can print bootstrap/workspace diagnostics before enabling JSON mode.
                // Ignore plain text, but still reject damaged structured events and require a result.
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed[0] is not ('{' or '[')) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid Hermes stream event.");
                var id = String(root, "session_id");
                if (!string.IsNullOrWhiteSpace(id)) _sessionId = id;
                switch (String(root, "type"))
                {
                    case "text":
                        var text = String(root, "text");
                        if (text.Length > 0) { emittedText = true; writer.TryWrite(new AgentEvent.AssistantText(text)); }
                        break;
                    case "tool_use":
                        writer.TryWrite(new AgentEvent.ToolUse(String(root, "name"), root.TryGetProperty("input", out var input) ? input.GetRawText() : ""));
                        break;
                    case "tool_result":
                        var toolId = String(root, "tool_call_id");
                        writer.TryWrite(new AgentEvent.ToolResult(toolId.Length > 0 ? toolId : String(root, "name"), root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True));
                        break;
                    case "result":
                        terminal = true;
                        if (root.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var value) && value != 0 || !string.IsNullOrEmpty(String(root, "error")))
                            failure = String(root, "error") is { Length: > 0 } detail ? detail : "Hermes reported a failed turn.";
                        if (!emittedText && String(root, "text") is { Length: > 0 } final) { emittedText = true; writer.TryWrite(new AgentEvent.AssistantText(final)); }
                        break;
                }
            }
            await process.WaitForExitAsync(ct);
            ct.ThrowIfCancellationRequested();
            var stderr = await stderrTask;
            if (stderr.Length > 0) _logger.LogDebug("hermes stderr: {Text}", stderr);
            if (process.ExitCode != 0) failure ??= $"Hermes exited with code {process.ExitCode}: {stderr.Trim()}";
            if (!terminal) failure ??= "Hermes exited without a terminal result event.";
            if (_sessionId is null) failure ??= "Hermes did not report a session ID; continuation cannot be guaranteed.";
            if (failure is not null) throw new InvalidOperationException(failure);
        }
        finally { await owned.StopAsync(); await stderrTask; }
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static (int ExitCode, string Output) Capture(AgentLaunchOptions options, string[] args)
    {
        try { return CaptureAsync(options, args).GetAwaiter().GetResult(); }
        catch (Exception) { return (-1, ""); }
    }

    private static async Task<(int ExitCode, string Output)> CaptureAsync(AgentLaunchOptions options, string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var owned = OwnedAgentProcess.Start(options.CreateStartInfo(Executable, args), timeout.Token);
        owned.Process.StandardInput.Close();
        var stdout = owned.Process.StandardOutput.ReadToEndAsync();
        var stderr = owned.Process.StandardError.ReadToEndAsync();
        try
        {
            await owned.Process.WaitForExitAsync(timeout.Token);
            return (owned.Process.ExitCode, await stdout);
        }
        finally { await owned.StopAsync(); await stdout; await stderr; }
    }
}
