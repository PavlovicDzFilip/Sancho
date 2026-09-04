using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Agents;

/// <summary>
/// Agent backend for Nous Research's Hermes CLI. One process per turn using
/// the one-shot <c>hermes -z</c> mode (final answer text on stdout, nothing
/// else). Turns are chained by discovering the newest session id after the
/// first turn and passing <c>--pass-session-id</c> on subsequent calls.
/// Session-id discovery and listing parse the CLI's output best-effort —
/// hermes is not required to be installed for sancho to build or run other
/// agents, and failures degrade to fresh sessions.
/// </summary>
public sealed class HermesAgentService : AgentService
{
    private const string Executable = "hermes";

    // hermes session ids are UUIDs; this is how we pull ids out of CLI output.
    private static readonly Regex SessionIdPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private readonly string? _resumeSessionId;
    private readonly ILogger<HermesAgentService> _logger;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();

    private string? _sessionId;
    private volatile bool _ready;

    public HermesAgentService(string? resumeSessionId, ILogger<HermesAgentService> logger)
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

    /// <inheritdoc />
    public override IReadOnlyList<AgentService.SessionSummary> ListSessions(string targetDirectory) =>
        ListSessions();

    /// <summary>Lists recent sessions from <c>hermes sessions list</c> output, newest first.</summary>
    public static IReadOnlyList<AgentService.SessionSummary> ListSessions()
    {
        var lines = TryRunAndCapture(["sessions", "list"]);
        var sessions = new List<AgentService.SessionSummary>();
        foreach (var line in lines)
        {
            var match = SessionIdPattern.Match(line);
            if (!match.Success)
                continue;

            var preview = line.Replace(match.Value, "").Trim(' ', '|', '-', '\t');
            sessions.Add(new AgentService.SessionSummary(
                match.Value, DateTime.MinValue, null, preview.Length > 0 ? preview : "(session)"));
        }

        // hermes lists newest first already; LastActivity is unknown so keep
        // the CLI's order.
        return sessions;
    }

    /// <inheritdoc />
    public override IReadOnlyList<(bool IsUser, string Text)> GetSessionMessages()
    {
        var id = _resumeSessionId ?? _sessionId;
        if (id is null)
            return Array.Empty<(bool IsUser, string Text)>();

        var export = Path.Combine(Path.GetTempPath(), $"sancho-hermes-{Guid.NewGuid():N}.jsonl");
        try
        {
            var exit = TryRun(["sessions", "export", export, "--session-id", id], out _);
            if (exit != 0 || !File.Exists(export))
                return Array.Empty<(bool IsUser, string Text)>();

            return SessionJson.ReadMessages(export, SessionJson.Format.Auto);
        }
        finally
        {
            try { File.Delete(export); }
            catch (IOException) { /* temp file — best effort */ }
        }
    }

    /// <inheritdoc />
    public override void VerifyAvailable() => VerifyAvailable(Executable);

    /// <summary>Checks hermes is installed.</summary>
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
                    "hermes is not installed. Install it from hermes.dev and try again.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "Could not find `hermes`. Install it from hermes.dev and try again.");
        }
    }

    // ── Turn worker ────────────────────────────────────────────────

    private async Task WorkerAsync(ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        try
        {
            _ready = true;
            writer.TryWrite(AgentEvent.Ready.Instance);

            var firstTurn = true;
            await foreach (var sentence in _input.Reader.ReadAllAsync(ct))
            {
                _ready = false;
                writer.TryWrite(AgentEvent.TurnStart.Instance);

                var text = await RunTurnAsync(sentence, ct);
                if (text is not null)
                    writer.TryWrite(new AgentEvent.AssistantText(text));

                if (firstTurn && _resumeSessionId is null)
                {
                    // The one-shot reply carries no session id — discover the
                    // newest session so later turns continue it.
                    var discovered = DiscoverNewestSessionId();
                    if (discovered is not null)
                    {
                        _sessionId = discovered;
                        _logger.LogDebug("hermes session discovered: {SessionId}", discovered);
                    }
                }

                firstTurn = false;
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

    /// <summary>Runs one one-shot turn; returns the final answer text, or null on failure.</summary>
    private async Task<string?> RunTurnAsync(string sentence, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-z");
        psi.ArgumentList.Add(sentence);
        if (_sessionId is not null)
        {
            psi.ArgumentList.Add("--pass-session-id");
            psi.ArgumentList.Add(_sessionId);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {Executable} process.");
        _logger.LogDebug("→ {Executable}: {Text}", Executable, sentence);

        var stderrTask = ReadAllAsync(process.StandardError, ct);
        var stdout = await ReadAllAsync(process.StandardOutput, ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("hermes stderr: {Text}", stderr.Trim());

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
            return null;

        var text = stdout.Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>Finds the newest session id in `hermes sessions list` output.</summary>
    private static string? DiscoverNewestSessionId()
    {
        foreach (var line in TryRunAndCapture(["sessions", "list"]))
        {
            var match = SessionIdPattern.Match(line);
            if (match.Success)
                return match.Value;
        }

        return null;
    }

    // ── Process helpers ────────────────────────────────────────────

    private static async Task<string> ReadAllAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadToEndAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return "";
        }
    }

    private static int TryRun(string[] args, out string stdout)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                stdout = "";
                return -1;
            }

            stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
            return proc.ExitCode;
        }
        catch (Exception)
        {
            stdout = "";
            return -1;
        }
    }

    private static IReadOnlyList<string> TryRunAndCapture(string[] args)
    {
        var exit = TryRun(args, out var stdout);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return Array.Empty<string>();

        return stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }
}
