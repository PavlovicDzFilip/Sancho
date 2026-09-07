using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System.Text;
using Sancho.Console;
using Sancho.Console.Audio;
using Sancho.Console.Cli;
using Sancho.Console.Config;
using Sancho.Console.Logging;
using Sancho.Console.Orchestration;
using Sancho.Console.Agents;
using Sancho.Console.Transcription;
using Spectre.Console;
// Display is in the root namespace

// Unicode output: Windows consoles default to a legacy codepage and render
// sancho's emoji/box-drawing UI as '?'. UTF-8 also keeps redirected output
// (e.g. the --log pipe) decodable.
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
{
    // headless environment that can't change its encoding — degrade gracefully
}

// sherpa-onnx's native logger writes INFO noise to stderr; it reads the
// level once at first use, so set it here, before any sherpa code runs.
// WARNING keeps real errors. (The LOGE "Creating a resampler" message is
// avoided structurally: LocalTranscriptionService feeds the model its
// native 16 kHz, so sherpa never builds a resampler.)
Environment.SetEnvironmentVariable("SHERPA_ONNX_LOG_LEVEL", "WARNING");

// ── CLI surface: help, version, and config don't need any services ──
CliArgs cliArgs;
try
{
    cliArgs = CliArgs.Parse(args);
}
catch (UsageError ex)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    AnsiConsole.MarkupLine("[grey]Run 'sancho --help' for usage.[/]");
    return 2;
}

if (cliArgs.ShowHelp)
{
    AnsiConsole.Markup(HelpText.Body);
    return 0;
}

if (cliArgs.ShowVersion)
{
    AnsiConsole.MarkupLine(HelpText.Version);
    return 0;
}

if (cliArgs.Command is "config")
{
    try
    {
        return ConfigCommand.Handle(cliArgs.CommandArgs);
    }
    catch (Exception ex) when (ex is UsageError or ConfigException)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        return 2;
    }
}

static string? ChooseSession(IReadOnlyList<AgentService.SessionSummary> sessions)
{
    if (sessions.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No previous sessions found — starting fresh.[/]");
        return null;
    }

    var choices = new List<AgentService.SessionSummary>
    {
        new(string.Empty, DateTime.MinValue, null, "Start a fresh session")
    };
    choices.AddRange(sessions);

    var chosen = AnsiConsole.Prompt(
        new SelectionPrompt<AgentService.SessionSummary>()
            .Title("Choose a session to continue")
            .UseConverter(s => s.Id.Length == 0
                ? s.Preview
                : $"{s.LastActivity:yyyy-MM-dd HH:mm}  {Markup.Escape(s.Title ?? s.Preview)}")
            .AddChoices(choices));

    return chosen.Id.Length == 0 ? null : chosen.Id;
}

/// <summary>
/// Resolves which agent CLI to use, with precedence
/// <c>--agent</c> flag &gt; config file &gt; first-run PATH discovery.
/// Discovery: exactly one installed CLI is picked and persisted to the
/// config; several prompt the user with a selector (like the microphone
/// picker); none fails with install hints. Notes mode needs no agent, so
/// discovery is skipped there entirely. Returns <c>null</c> on fatal failure.
/// </summary>
static string? ResolveAgent(CliArgs cliArgs, SanchoConfig stored)
{
    var explicitName = (cliArgs.Agent ?? stored.Agent)?.ToLowerInvariant();
    if (explicitName is not null)
    {
        if (explicitName is not ("claude" or "cursor" or "hermes" or "codex"))
        {
            AnsiConsole.MarkupLine($"[red]Agent '{explicitName}' is not supported yet. Use 'claude', 'cursor', 'hermes' or 'codex'.[/]");
            return null;
        }
        return explicitName;
    }

    if (cliArgs.Notes)
        return null; // notes mode never involves an agent

    var available = AgentDetector.Supported
        .Where(a => AgentDetector.IsOnPath(a.Executable))
        .ToList();

    switch (available.Count)
    {
        case 0:
            AnsiConsole.MarkupLine("[red]No agent CLI found on your PATH. Install one, then run Sancho again:[/]");
            foreach (var a in AgentDetector.Supported)
                AnsiConsole.MarkupLine($"  [grey]{a.Name,-8}{Markup.Escape(a.InstallHint)}[/]");
            return null;

        case 1:
        {
            var only = available[0];
            ConfigStore.Save(stored with { Agent = only.Name });
            AnsiConsole.MarkupLine(
                $"[grey]No agent configured — found {only.Name}; using it. " +
                "Change it with 'sancho config set agent <name>'.[/]");
            return only.Name;
        }

        default:
        {
            var chosen = AnsiConsole.Prompt(
                new SelectionPrompt<(string Name, string Executable, string InstallHint)>()
                    .Title("Multiple agents found — choose the one to use")
                    .UseConverter(a => $"{a.Name}  ({Markup.Escape(a.InstallHint)})")
                    .AddChoices(available));
            ConfigStore.Save(stored with { Agent = chosen.Name });
            AnsiConsole.MarkupLine(
                $"[grey]Saved 'agent: {chosen.Name}' — change it with 'sancho config set agent <name>'.[/]");
            return chosen.Name;
        }
    }
}

/// <summary>
/// Creates the agent backend. The factory guarantees a usable executable:
/// not installed or not logged in fails here, before the orchestrator starts.
/// </summary>
static AgentService CreateAgent(string agentName, string? resumeSessionId, IServiceProvider sp) => agentName switch
{
    "claude" => CreateClaudeAgent(resumeSessionId, sp),
    "cursor" => CreateCursorAgent(resumeSessionId, sp),
    "hermes" => CreateHermesAgent(resumeSessionId, sp),
    "codex" => CreateCodexAgent(resumeSessionId, sp),
    _ => throw new InvalidOperationException($"Agent '{agentName}' is not supported yet."),
};

static AgentService CreateClaudeAgent(string? resumeSessionId, IServiceProvider sp)
{
    ClaudeCodeAgentService.VerifyAvailable("claude");
    return new ClaudeCodeAgentService(
        "claude", null, resumeSessionId,
        sp.GetRequiredService<ILogger<ClaudeCodeAgentService>>());
}

static AgentService CreateCursorAgent(string? resumeSessionId, IServiceProvider sp)
{
    CursorAgentService.VerifyAvailable();
    return new CursorAgentService(
        resumeSessionId, sp.GetRequiredService<ILogger<CursorAgentService>>());
}

static AgentService CreateHermesAgent(string? resumeSessionId, IServiceProvider sp)
{
    HermesAgentService.VerifyAvailable();
    return new HermesAgentService(
        resumeSessionId, sp.GetRequiredService<ILogger<HermesAgentService>>());
}

static AgentService CreateCodexAgent(string? resumeSessionId, IServiceProvider sp)
{
    CodexAgentService.VerifyAvailable();
    return new CodexAgentService(
        resumeSessionId, sp.GetRequiredService<ILogger<CodexAgentService>>());
}

/// <summary>Session list for the selected agent, for the --continue picker.</summary>
static IReadOnlyList<AgentService.SessionSummary> ListAgentSessions(string agentName, string targetDirectory) => agentName switch
{
    "cursor" => CursorAgentService.ListSessions(CursorAgentService.DefaultSessionRoot(), targetDirectory),
    "hermes" => HermesAgentService.ListSessions(),
    "codex" => CodexAgentService.ListAllSessions(CodexAgentService.DefaultSessionRoot()),
    _ => ClaudeCodeAgentService.ListSessions(ClaudeCodeAgentService.DefaultSessionRoot(), targetDirectory),
};

// ── Run path ───────────────────────────────────────────────────────

async Task<int> Run(LogFileWriter? logFile)
{
    if (cliArgs.Meeting && !OperatingSystem.IsWindows())
    {
        AnsiConsole.MarkupLine("[red]--meeting is Windows-only for now (loopback capture); Linux/macOS coming later.[/]");
        return 2;
    }

    // ── Resolve configuration (defaults < ~/.sancho/config.json < flags) ──
    var stored = ConfigStore.Load();

    // The agent backend: config key or --agent flag. When neither is set,
    // first-run discovery probes the PATH (see ResolveAgent below) — claude
    // is no longer silently assumed. Null here is fatal, except in notes
    // mode, which never involves an agent.
    var agentName = ResolveAgent(cliArgs, stored);
    if (agentName is null && !cliArgs.Notes)
        return 2;

    // The whisper model size: config key or --model flag; small is the default.
    var modelName = (cliArgs.Model ?? stored.Model ?? WhisperModels.DefaultSize).ToLowerInvariant();
    var whisperSpec = WhisperModels.TryGet(modelName);
    if (whisperSpec is null)
    {
        AnsiConsole.MarkupLine($"[red]Model '{modelName}' is not supported. Use 'tiny', 'base', 'small' or 'medium'.[/]");
        return 2;
    }

    // ffmpeg is the capture backend on Linux/macOS; Windows uses the bundled
    // NAudio package and needs nothing beyond the .NET runtime.
    if (!OperatingSystem.IsWindows())
    {
        try
        {
            FfmpegAudioSource.VerifyFfmpegAvailable();
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 2;
        }
    }

    var targetDir = Directory.GetCurrentDirectory();

    // Notes mode only transcribes — no system prompt file, no agent session.
    // The agent factory (below) verifies the CLI is installed and logged in.
    if (!cliArgs.Notes)
    {
        try
        {
            var (promptPath, promptCreated) = ClaudeCodeAgentService.EnsureSystemPrompt(targetDir);
            if (promptCreated)
                AnsiConsole.MarkupLine(
                    $"[grey]Created {Path.GetFileName(promptPath)} with the default system prompt — edit it to customize.[/]");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 2;
        }
    }

    // ── Composition root ──────────────────────────────────────────────
    var services = new ServiceCollection();
    services.AddLogging(builder =>
    {
        builder.ClearProviders();
        builder.AddConsole(options => options.FormatterName = "raw");
        if (logFile is not null)
        {
            // The file logger carries the Debug/Trace diagnostics the console
            // hides; the console mirror already carries Information and above.
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Information);
        }
        else
        {
            builder.SetMinimumLevel(LogLevel.Information);
        }
    });
    if (logFile is not null)
        services.AddSingleton<ILoggerProvider>(new FileLogProvider(logFile));
    services.AddSingleton<ConsoleFormatter, RawConsoleFormatter>();
    services.AddSingleton(stored);
    services.AddSingleton<Display>();
    services.AddSingleton<MicLevelMonitor>();
    services.AddSingleton<AudioSourceFactory>();
    services.AddSingleton<WhisperModelSpec>(whisperSpec); // resolved above: defaults < config < flags
    services.AddSingleton<LocalSttModels>();
    services.AddSingleton<LocalTranscriptionService>();

    services.AddSingleton(cliArgs);

    if (cliArgs.Notes)
    {
        // Notes mode never involves Claude — no CLI check, no session, no
        // .sancho.md side effects. The orchestrator gets a null ClaudeService.
        services.AddSingleton<Orchestrator>(sp => new Orchestrator(
            sp.GetRequiredService<AudioSourceFactory>(),
            sp.GetRequiredService<LocalTranscriptionService>(),
            sp.GetRequiredService<CliArgs>(),
            null,
            sp.GetRequiredService<Display>(),
            sp.GetRequiredService<MicLevelMonitor>(),
            sp.GetRequiredService<ILogger<Orchestrator>>()));
    }
    else
    {
        // agentName is non-null here: ResolveAgent returned null and !Notes
        // returned from Run above.
        var resumeSessionId = cliArgs.Continue
            ? ChooseSession(ListAgentSessions(agentName!, targetDir))
            : null;

        services.AddSingleton<AgentService>(sp =>
            CreateAgent(agentName!, resumeSessionId, sp));
        services.AddSingleton<Orchestrator>();
    }

    try
    {
        using var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();

        // ── Model download ─────────────────────────────────────────────
        // The selected whisper size was resolved above; download it now, at
        // the very start, so the run never stalls mid-transcription waiting
        // for model files. (The transcription service re-checks later, but
        // that is a fast path once the files are on disk.)
        try
        {
            var models = provider.GetRequiredService<LocalSttModels>();
            if (await models.EnsureDownloadedAsync(cts.Token) is null)
            {
                AnsiConsole.MarkupLine(
                    "[red]Could not download the speech model — check your connection and restart Sancho.[/]");
                return 2;
            }
        }
        catch (OperationCanceledException)
        {
            return 0; // cancelled before the run began (Ctrl+C)
        }

        var orchestrator = provider.GetRequiredService<Orchestrator>();

        await orchestrator.RunAsync(cts.Token);

        return 0;
    }
    catch (InvalidOperationException ex)
    {
        // e.g. the agent factory's VerifyAvailable failed.
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        return 2;
    }
}

LogFileWriter? logFile = null;
if (cliArgs.Log)
{
    logFile = new LogFileWriter(SanchoPaths.LogFile);
    var previousOut = Console.Out;
    Console.SetOut(new TeeTextWriter(previousOut, logFile));
    try
    {
        return await Run(logFile);
    }
    finally
    {
        Console.SetOut(previousOut);
        logFile.Dispose();
    }
}

return await Run(null);
