using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sancho.Console;

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

builder.Services.AddSingleton<RealtimeTranscriptionService>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<MicrophoneAudioSource>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MicrophoneAudioSource>>();
    return MicrophoneAudioSource.Create(logger);
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var transcriptionService = host.Services.GetRequiredService<RealtimeTranscriptionService>();
var claudeService = host.Services.GetRequiredService<ClaudeService>();
using var micSource = host.Services.GetRequiredService<MicrophoneAudioSource>();

// ── Pipeline: mic → channel → transcription → console + claude ────
var channel = Channel.CreateUnbounded<byte[]>();

using var cts = new CancellationTokenSource();
var captureTask = micSource.CaptureAsync(channel.Writer, cts.Token);
var claudeTask = claudeService.RunAsync(cts.Token);

logger.LogInformation("🎤 Live transcription + Claude assistant started.");
logger.LogInformation("   Speak naturally. Press any key to stop.");
logger.LogInformation("───");

var transcribeTask = Task.Run(async () =>
{
    await foreach (var chunk in transcriptionService.TranscribeAsync(channel.Reader, cts.Token))
    {
        if (chunk.IsComplete)
        {
            logger.LogInformation(""); // finish the line
            var sentence = chunk.Text.TrimStart('\n').Trim();
            if (!string.IsNullOrWhiteSpace(sentence))
                claudeService.Enqueue(sentence);
        }
        else
        {
            logger.InfoInline("{0}", chunk.Text);
        }
    }
});

// ── Wait for user to stop ─────────────────────────────────────────
System.Console.ReadKey(intercept: true);
logger.LogInformation("Stopping...");
cts.Cancel();

await Task.WhenAll(captureTask, transcribeTask, claudeTask);

logger.LogInformation("✅ Done.");
return 0;
