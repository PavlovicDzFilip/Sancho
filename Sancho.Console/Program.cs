using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
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

// ── Verify prerequisites ──────────────────────────────────────────
ClaudeService.VerifyClaudeAvailable();

// ── Resolve configuration (defaults < ~/.sancho/config.json < env < flags) ──
var stored = ConfigStore.Load();

var apiKey = cliArgs.ApiKey
    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? stored.ApiKey;

if (string.IsNullOrWhiteSpace(apiKey))
{
    // First run: ask once, store it, and reuse it from ~/.sancho/config.json.
    apiKey = AnsiConsole.Prompt(
        new TextPrompt<string>("OpenAI API key not found — enter it now (https://platform.openai.com/api-keys):")
            .Secret()
            .Validate(value => string.IsNullOrWhiteSpace(value)
                ? ValidationResult.Error("API key cannot be empty.")
                : ValidationResult.Success()));

    ConfigStore.Save(ConfigStore.WithKey(stored, "apiKey", apiKey));
    AnsiConsole.MarkupLine($"[grey]Stored in {SanchoPaths.ConfigFile}[/]");
}

var targetDir = Directory.GetCurrentDirectory();

var transcriptionOptions = new TranscriptionOptions { ApiKey = apiKey };

// ── Composition root ──────────────────────────────────────────────
var services = new ServiceCollection();
services.AddLogging(builder =>
{
    builder.ClearProviders();
    builder.AddConsole(options => options.FormatterName = "raw");
    builder.SetMinimumLevel(LogLevel.Information);
});
services.AddSingleton<ConsoleFormatter, RawConsoleFormatter>();
services.AddSingleton(transcriptionOptions);
services.AddSingleton<Display>();
services.AddSingleton<MicrophoneAudioSourceFactory>();
services.AddSingleton<RealtimeTranscriptionService>();
services.AddSingleton<ITranscriptionService>(sp =>
    sp.GetRequiredService<RealtimeTranscriptionService>());
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
