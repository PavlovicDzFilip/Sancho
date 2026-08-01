using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sancho.Console.Audio;
using Sancho.Console.Logging;
using Sancho.Console.Orchestration;
using Sancho.Console.Services;
using Sancho.Console.Transcription;

// ── Verify prerequisites ──────────────────────────────────────────
ClaudeService.VerifyClaudeAvailable();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = "raw");
builder.Logging.AddConsoleFormatter<RawConsoleFormatter, Microsoft.Extensions.Logging.Console.SimpleConsoleFormatterOptions>();
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddOptions<TranscriptionOptions>()
    .Bind(builder.Configuration.GetSection("Transcription"))
    .Validate(opt => !string.IsNullOrWhiteSpace(opt.ApiKey),
        "OpenAI API key is required. Set 'Transcription:ApiKey' in appsettings.json.")
    .ValidateOnStart();

builder.Services.AddOptions<ClaudeOptions>()
    .Bind(builder.Configuration.GetSection("Claude"));

builder.Services.AddSingleton<MicrophoneAudioSourceFactory>();
builder.Services.AddSingleton<IAudioSource>(sp =>
    sp.GetRequiredService<MicrophoneAudioSourceFactory>().Create());
builder.Services.AddSingleton<RealtimeTranscriptionService>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<Orchestrator>();

var host = builder.Build();

using var cts = new CancellationTokenSource();
var orchestrator = host.Services.GetRequiredService<Orchestrator>();

await orchestrator.RunAsync(cts.Token);

return 0;
