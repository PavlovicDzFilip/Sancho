using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sancho.Console;

// ── Verify prerequisites ──────────────────────────────────────────
ClaudeService.VerifyClaudeAvailable();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

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

var transcriptionService = host.Services.GetRequiredService<RealtimeTranscriptionService>();
var claudeService = host.Services.GetRequiredService<ClaudeService>();
using var micSource = host.Services.GetRequiredService<MicrophoneAudioSource>();

// ── Pipeline: mic → channel → transcription → console + claude ────
var channel = Channel.CreateUnbounded<byte[]>();

using var cts = new CancellationTokenSource();
var captureTask = micSource.CaptureAsync(channel.Writer, cts.Token);
var claudeTask = claudeService.RunAsync(cts.Token);

Console.WriteLine("🎤 Live transcription + Claude assistant started.");
Console.WriteLine("   Speak naturally. Press any key to stop.");
Console.WriteLine("───");

var transcribeTask = Task.Run(async () =>
{
    await foreach (var chunk in transcriptionService.TranscribeAsync(channel.Reader, cts.Token))
    {
        Console.Write(chunk.Text);
        if (chunk.IsComplete)
        {
            var sentence = chunk.Text.TrimStart('\n').Trim();
            if (!string.IsNullOrWhiteSpace(sentence))
                claudeService.Enqueue(sentence);
        }
    }
});

// ── Wait for user to stop ─────────────────────────────────────────
Console.ReadKey(intercept: true);
Console.WriteLine("\nStopping...");
cts.Cancel();

await Task.WhenAll(captureTask, transcribeTask, claudeTask);

Console.WriteLine("✅ Done.");
return 0;
