using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sancho.Console;
using Sancho.Console.Audio;
using Sancho.Console.Orchestration;
using Sancho.Console.Services;
using Sancho.Console.Transcription;
// Display is in the root namespace

// ── Parse command-line flags ──────────────────────────────────────
var continueSession = args.Contains("--continue") || args.Contains("-c");
var listSessions = args.Contains("--list-sessions") || args.Contains("-l");

string? resumeSessionId = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--resume" || args[i] == "-r")
    {
        resumeSessionId = args[i + 1];
        break;
    }
}

// ── Verify prerequisites (skipped for the read-only list command) ──
if (!listSessions)
    ClaudeService.VerifyClaudeAvailable();

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

builder.Services.Configure<ClaudeOptions>(o =>
{
    o.ContinueSession = continueSession;
    o.ResumeSessionId = resumeSessionId ?? "";
});

builder.Services.AddSingleton<Display>();
builder.Services.AddSingleton<MicrophoneAudioSourceFactory>();
builder.Services.AddSingleton<RealtimeTranscriptionService>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<Orchestrator>();

var targetDir = builder.Configuration["Claude:TargetDirectory"] ?? "";
var host = builder.Build();

if (listSessions)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sancho.Sessions");
    var sessions = ClaudeService.ListSessions(targetDir);

    if (sessions.Count == 0)
    {
        logger.LogInformation("No sessions found for {Directory}.",
            string.IsNullOrWhiteSpace(targetDir) ? Environment.CurrentDirectory : targetDir);
    }
    else
    {
        logger.LogInformation("Sessions for {Directory}:",
            string.IsNullOrWhiteSpace(targetDir) ? Environment.CurrentDirectory : targetDir);
        foreach (var s in sessions)
            logger.LogInformation("{Id}  {Time}  {Preview}",
                s.Id, s.LastActivity.ToString("yyyy-MM-dd HH:mm"), s.Preview);
    }

    return 0;
}

using var cts = new CancellationTokenSource();
var orchestrator = host.Services.GetRequiredService<Orchestrator>();

await orchestrator.RunAsync(cts.Token);

return 0;
