using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private int _ready;
    private readonly AgentLaunchOptions _launchOptions;

    public CursorAgentService(string? resumeSessionId, ILogger<CursorAgentService> logger, AgentLaunchOptions? launchOptions = null)
    {
        _targetDirectory = Path.GetFullPath((launchOptions ?? new AgentLaunchOptions()).WorkingDirectory);
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

    internal sealed record CliLaunch(string Executable, IReadOnlyList<string> PrefixArguments, string? InvokedAs = null)
    {
        public ProcessStartInfo CreateStartInfo(AgentLaunchOptions options)
        {
            var info = options.CreateStartInfo(Executable, PrefixArguments);
            if (InvokedAs is not null) info.Environment["CURSOR_INVOKED_AS"] = InvokedAs;
            return info;
        }
    }

    private static CliLaunch ResolveLaunch() => ResolveLaunch(
        SearchDirectories(Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) : null,
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cursor-agent") : null),
        OperatingSystem.IsWindows(), launch => CheckLaunch(launch, "--help", requireAgentHelp: true));

    internal static IEnumerable<string> SearchDirectories(string? processPath, string? userPath, string? installDirectory) =>
        new[] { processPath, userPath }.Where(path => path is not null)
            .SelectMany(path => path!.Split(Path.PathSeparator))
            .Append(installDirectory ?? "").Select(path => path.Trim().Trim('"'))
            .Where(path => path.Length > 0).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static CliLaunch ResolveLaunch(IEnumerable<string> directories, bool windows, Func<CliLaunch, bool> isAgent)
    {
        var paths = directories.ToArray();
        var extensions = windows ? new[] { ".exe", ".cmd", ".ps1", ".bat", "" } : new[] { "" };
        // Names take priority over directories: prefer the unambiguous name,
        // then the modern agent name, and only accept cursor if it is the CLI.
        foreach (var name in new[] { "cursor-agent", "agent", "cursor" })
            foreach (var directory in paths)
                foreach (var extension in extensions)
                {
                    var path = Path.Combine(directory, name + extension);
                    if (!File.Exists(path)) continue;
                    var launch = windows && extension is ".cmd" or ".ps1" or ".bat"
                        ? BundledWindowsLaunch(path) : new CliLaunch(path, Array.Empty<string>());
                    if (launch is not null && isAgent(launch)) return launch;
                }
        throw new InvalidOperationException("Could not find Cursor's agent CLI (`cursor-agent`, `agent`, or an agent-capable `cursor`). Install it from cursor.com and try again.");
    }

    internal static CliLaunch? BundledWindowsLaunch(string wrapper)
    {
        // The official Windows shim delegates to node.exe + index.js. Launch
        // those directly so cmd/legacy PowerShell cannot reinterpret prompts.
        var directory = Path.GetDirectoryName(Path.GetFullPath(wrapper))!;
        var candidates = new List<string> { directory };
        var versions = Path.Combine(directory, "versions");
        if (Directory.Exists(versions))
            candidates.AddRange(Directory.GetDirectories(versions)
                .Select(path => (Path: path, Match: Regex.Match(Path.GetFileName(path), @"^(\d{4})\.(\d{1,2})\.(\d{1,2})(-\d{2}-\d{2}-\d{2})?-[a-f0-9]+$")))
                .Where(item => item.Match.Success)
                .OrderByDescending(item => int.Parse(item.Match.Groups[1].Value))
                .ThenByDescending(item => int.Parse(item.Match.Groups[2].Value))
                .ThenByDescending(item => int.Parse(item.Match.Groups[3].Value))
                .ThenByDescending(item => item.Match.Groups[4].Value, StringComparer.Ordinal)
                .Select(item => item.Path));
        foreach (var candidate in candidates)
        {
            var node = Path.Combine(candidate, "node.exe");
            var entrypoint = Path.Combine(candidate, "index.js");
            if (File.Exists(node) && File.Exists(entrypoint))
                return new CliLaunch(node, new[] { entrypoint }, Path.GetFileName(wrapper));
        }
        return null;
    }

    private static bool CheckLaunch(CliLaunch launch, string argument, bool requireAgentHelp)
    {
        try
        {
            var psi = launch.CreateStartInfo(new AgentLaunchOptions());
            psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return false;
            }
            var help = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 && (!requireAgentHelp || IsAgentHelp(help));
        }
        catch (System.ComponentModel.Win32Exception) { }
        return false;
    }

    internal static bool IsAgentHelp(string help) => help.Contains("Cursor", StringComparison.OrdinalIgnoreCase)
        && help.Contains("--output-format", StringComparison.Ordinal) && help.Contains("--resume", StringComparison.Ordinal)
        && help.Contains("stream-json", StringComparison.Ordinal);

    /// <summary>Checks cursor-agent is installed (auth failures surface on the first turn).</summary>
    public static void VerifyAvailable(string executable = Executable)
    {
        if (executable == Executable)
        {
            if (!CheckLaunch(ResolveLaunch(), "--version", requireAgentHelp: false))
                throw new InvalidOperationException("Cursor CLI failed its --version check.");
            return;
        }
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
            if (proc is null)
                throw new InvalidOperationException(
                    "cursor-agent is not installed. Install it from cursor.com and try again.");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5_000))
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit();
                throw new InvalidOperationException("Cursor CLI did not respond to --version.");
            }
            stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();
            if (proc.ExitCode != 0) throw new InvalidOperationException("Cursor CLI failed its --version check.");
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
        var psi = _launchOptions.Executable is null
            ? ResolveLaunch().CreateStartInfo(_launchOptions)
            : _launchOptions.CreateStartInfo(Executable, []);
        psi.ArgumentList.Add("-p");
        // Cursor has no documented append-system-prompt option. Keep its native
        // guidance and supply Sancho's instructions in the user prompt instead.
        var instructions = _launchOptions.ReadInstructions();
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(instructions)
            ? "Spoken user request:\n" + sentence
            : "<sancho_instructions>\n" + instructions + "\n</sancho_instructions>\nSpoken user request:\n" + sentence);
        psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add("--trust");
        psi.ArgumentList.Add("--approve-mcps");
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("disabled");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        if (_sessionId is not null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(_sessionId);
        }

        await using var owned = OwnedAgentProcess.Start(psi, ct);
        var process = owned.Process;
        process.StandardInput.Close();
        _logger.LogDebug("→ {Executable}: {Text}", Executable, sentence);

        // stderr is drained concurrently so a chatty process can't deadlock
        // on a full pipe buffer.
        var stderrTask = ReadStderrAsync(process, writer, ct);
        var state = new TurnState();
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
                        HandleEvent(doc.RootElement, writer, state);
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
                else if (!state.Terminal && !ct.IsCancellationRequested)
                    writer.TryWrite(new AgentEvent.Error("Cursor ended without a terminal result."));
        }
        finally
        {
            await owned.StopAsync();
            await stderrTask;
        }
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

    private sealed class TurnState
    {
        public bool Terminal;
        public bool HasAssistantText;
    }

    private void HandleEvent(JsonElement root, ChannelWriter<AgentEvent> writer, TurnState state)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var type = String(root, "type");
        if (String(root, "session_id") is { Length: > 0 } eventSessionId)
        {
            if (_sessionId is not null && _sessionId != eventSessionId)
                throw new InvalidOperationException("Cursor returned a different session ID; refusing to switch conversations.");
            _sessionId = eventSessionId;
        }

        switch (type)
        {
            case "system":
                break;

            case "assistant":
                if (root.TryGetProperty("message", out var message))
                {
                    var text = SessionJson.ExtractText(message);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        state.HasAssistantText = true;
                        writer.TryWrite(new AgentEvent.AssistantText(text));
                    }
                }
                break;

            case "tool_call":
                EmitToolCall(root, writer);
                break;

            case "result":
                state.Terminal = true;
                if (IsError(root) || String(root, "subtype") is { } subtype && subtype != "success")
                    writer.TryWrite(new AgentEvent.Error(String(root, "result") ?? String(root, "error") ?? "Cursor turn failed."));
                else if (!state.HasAssistantText && String(root, "result") is { Length: > 0 } result)
                {
                    state.HasAssistantText = true;
                    writer.TryWrite(new AgentEvent.AssistantText(result));
                }
                break;
            case "error":
                writer.TryWrite(new AgentEvent.Error(String(root, "message") ?? String(root, "error") ?? "Cursor reported an error."));
                break;
        }
    }

    /// <summary>Cursor reports tools as <c>tool_call</c> events (started/completed).</summary>
    private static void EmitToolCall(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var subtype = root.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;
        if (subtype is not ("started" or "completed"))
            return;

        var name = "tool";
        JsonElement detail = default;
        if (root.TryGetProperty("tool_call", out var tc) && tc.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in tc.EnumerateObject())
            {
                name = prop.Name;
                detail = prop.Value;
                if (name == "function") name = String(detail, "name") ?? name;
                break;
            }
        }

        if (subtype == "started")
            writer.TryWrite(new AgentEvent.ToolUse(name,
                detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("args", out var args)
                    ? args.GetRawText() : String(detail, "arguments") ?? name));
        else
        {
            var failed = IsError(root) || IsError(detail);
            if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("result", out var result))
            {
                failed |= IsError(result);
                if (failed) writer.TryWrite(new AgentEvent.Status(result.GetRawText(), AgentStatusKind.Info));
            }
            writer.TryWrite(new AgentEvent.ToolResult(String(root, "call_id") ?? name, failed));
        }
    }

    private static string? String(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsError(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
        ((root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True)
        || (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        || (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
        || (root.TryGetProperty("failure", out var failure) && failure.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)));

    private string? ResolveSessionFile()
    {
        return ResolveSessionFile(DefaultSessionRoot(), _targetDirectory, _sessionId);
    }

    internal static string? ResolveSessionFile(string sessionRoot, string targetDirectory, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var dir = Path.Combine(sessionRoot, "projects",
            SessionJson.EncodeProjectDirectory(targetDirectory), "agent-transcripts");
        var path = Path.Combine(dir, sessionId, sessionId + ".jsonl");
        return File.Exists(path) ? path : null;
    }
}
