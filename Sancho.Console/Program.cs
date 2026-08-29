using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sancho.Console;
using Sancho.Console.Audio;
using Sancho.Console.Orchestration;
using Sancho.Console.Services;
using Sancho.Console.Transcription;
using Spectre.Console;
// Display is in the root namespace

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

var continueSession = args.Contains("--continue") || args.Contains("-c");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = "raw");
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddOptions<TranscriptionOptions>()
    .Bind(builder.Configuration.GetSection("Transcription"))
    .Validate(opt => !string.IsNullOrWhiteSpace(opt.ApiKey),
        "OpenAI API key is required. Set 'Transcription:ApiKey' in appsettings.json.")
    .ValidateOnStart();

builder.Services.AddOptions<ClaudeOptions>()
    .Bind(builder.Configuration.GetSection("Claude"));

builder.Services.AddSingleton<Display>();
builder.Services.AddSingleton<MicrophoneAudioSourceFactory>();
builder.Services.AddSingleton<RealtimeTranscriptionService>();
builder.Services.AddSingleton<ITranscriptionService>(sp =>
    sp.GetRequiredService<RealtimeTranscriptionService>());
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
builder.Services.AddSingleton<SessionTitleService>();

var targetDir = ClaudeService.ResolveTargetDirectory(builder.Configuration["Claude:TargetDirectory"]);
var resumeSessionId = continueSession ? ChooseSession(targetDir) : null;

builder.Services.Configure<ClaudeOptions>(o => o.TargetDirectory = targetDir);
builder.Services.AddSingleton<ClaudeService>(sp =>
    new ClaudeService(
        sp.GetRequiredService<IOptions<ClaudeOptions>>(),
        resumeSessionId,
        sp.GetRequiredService<ILogger<ClaudeService>>(),
        sp.GetRequiredService<SessionTitleService>()));
builder.Services.AddSingleton<Orchestrator>();

var host = builder.Build();

using var cts = new CancellationTokenSource();
var orchestrator = host.Services.GetRequiredService<Orchestrator>();

await orchestrator.RunAsync(cts.Token);

return 0;
