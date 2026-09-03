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
using Sancho.Console.Services;
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

static string? ChooseSession(string targetDirectory)
{
    var sessions = ClaudeService.ListSessions(targetDirectory);

    if (sessions.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No previous sessions found — starting fresh.[/]");
        return null;
    }

    var choices = new List<ClaudeService.SessionSummary>
    {
        new(string.Empty, DateTime.MinValue, null, "Start a fresh session")
    };
    choices.AddRange(sessions);

    var chosen = AnsiConsole.Prompt(
        new SelectionPrompt<ClaudeService.SessionSummary>()
            .Title("Choose a session to continue")
            .UseConverter(s => s.Id.Length == 0
                ? s.Preview
                : $"{s.LastActivity:yyyy-MM-dd HH:mm}  {Markup.Escape(s.Title ?? s.Preview)}")
            .AddChoices(choices));

    return chosen.Id.Length == 0 ? null : chosen.Id;
}

// ── Run path ───────────────────────────────────────────────────────

async Task<int> Run(LogFileWriter? logFile)
{
    // ── Verify prerequisites ──────────────────────────────────────────
    try
    {
        ClaudeService.VerifyClaudeAvailable();
    }
    catch (InvalidOperationException ex)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
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

    try
    {
        var (promptPath, promptCreated) = ClaudeService.EnsureSystemPrompt(targetDir);
        if (promptCreated)
            AnsiConsole.MarkupLine(
                $"[grey]Created {Path.GetFileName(promptPath)} with the default system prompt — edit it to customize.[/]");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        return 2;
    }

    // ── Resolve configuration (defaults < ~/.sancho/config.json < env < flags) ──
    var stored = ConfigStore.Load();

    // Transcription backend: openai is the default; record writes a local WAV;
    // local transcribes on-device with sherpa-onnx (no cloud round-trip).
    var mode = (cliArgs.Transcription ?? stored.Transcription ?? TranscriptionOptions.OpenAi)
        .ToLowerInvariant();
    if (mode is not (TranscriptionOptions.OpenAi or TranscriptionOptions.Record or TranscriptionOptions.Local))
    {
        AnsiConsole.MarkupLine($"[red]Unknown transcription mode '{mode}'. Use 'openai', 'record' or 'local'.[/]");
        return 2;
    }

    var apiKey = cliArgs.ApiKey
        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        ?? stored.ApiKey;

    // Only openai mode needs a key for transcription; local/record use it
    // (if present) just for OpenAI-generated session titles.
    if (mode == TranscriptionOptions.OpenAi && string.IsNullOrWhiteSpace(apiKey))
    {
        // First run in openai mode: ask once, store it, and reuse it from ~/.sancho/config.json.
        apiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("OpenAI API key not found — enter it now (https://platform.openai.com/api-keys):")
                .Secret()
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("API key cannot be empty.")
                    : ValidationResult.Success()));

        ConfigStore.Save(ConfigStore.WithKey(stored, "apiKey", apiKey));
        AnsiConsole.MarkupLine($"[grey]Stored in {SanchoPaths.ConfigFile}[/]");
    }

    var transcriptionOptions = new TranscriptionOptions { ApiKey = apiKey ?? "", Mode = mode };

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
    services.AddSingleton(transcriptionOptions);
    services.AddSingleton<Display>();
    services.AddSingleton<MicLevelMonitor>();
    services.AddSingleton<AudioSourceFactory>();
    services.AddSingleton<RealtimeTranscriptionService>();
    services.AddSingleton<RecordingTranscriptionService>();
    services.AddSingleton<LocalSttModels>();
    services.AddSingleton<LocalTranscriptionService>();
    services.AddSingleton<ITranscriptionService>(sp => mode switch
    {
        TranscriptionOptions.Record => sp.GetRequiredService<RecordingTranscriptionService>(),
        TranscriptionOptions.Local => sp.GetRequiredService<LocalTranscriptionService>(),
        _ => sp.GetRequiredService<RealtimeTranscriptionService>(),
    });
    services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
    services.AddSingleton<SessionTitleService>();

    var resumeSessionId = cliArgs.Continue ? ChooseSession(targetDir) : null;

    services.AddSingleton<ClaudeService>(sp =>
        new ClaudeService(
            resumeSessionId,
            sp.GetRequiredService<ILogger<ClaudeService>>(),
            sp.GetRequiredService<SessionTitleService>()));
    services.AddSingleton<Orchestrator>();

    using var provider = services.BuildServiceProvider();

    using var cts = new CancellationTokenSource();
    var orchestrator = provider.GetRequiredService<Orchestrator>();

    await orchestrator.RunAsync(cts.Token);

    return 0;
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
