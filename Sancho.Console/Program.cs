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

    // The agent backend: config key or --agent flag; claude is the default.
    // More agents (cursor, codex, hermes) land behind the same seam later.
    var agentName = (cliArgs.Agent ?? stored.Agent ?? "claude").ToLowerInvariant();
    if (agentName is not ("claude" or "cursor" or "hermes" or "codex"))
    {
        AnsiConsole.MarkupLine($"[red]Agent '{agentName}' is not supported yet. Use 'claude', 'cursor', 'hermes' or 'codex'.[/]");
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
        var resumeSessionId = cliArgs.Continue
            ? ChooseSession(ListAgentSessions(agentName, targetDir))
            : null;

        services.AddSingleton<AgentService>(sp =>
            CreateAgent(agentName, resumeSessionId, sp));
        services.AddSingleton<Orchestrator>();
    }

    try
    {
        using var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
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
