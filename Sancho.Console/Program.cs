using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sancho;
using Sancho.Console;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

builder.Services.AddOptions<TranscriptionOptions>()
    .Bind(builder.Configuration.GetSection("Transcription"))
    .Validate(opt => !string.IsNullOrWhiteSpace(opt.ApiKey),
        "OpenAI API key is required. Set 'Transcription:ApiKey' in appsettings.json.")
    .ValidateOnStart();

builder.Services.AddSingleton<RealtimeTranscriptionService>();
builder.Services.AddSingleton<MicrophoneAudioSource>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MicrophoneAudioSource>>();
    return MicrophoneAudioSource.Create(logger);
});

var host = builder.Build();

// ValidateOnStart fires during Build() above — throws if ApiKey is missing
var transcriptionService = host.Services.GetRequiredService<RealtimeTranscriptionService>();
using var micSource = host.Services.GetRequiredService<MicrophoneAudioSource>();

// --- Pipeline: mic → channel → transcription ---
var channel = Channel.CreateUnbounded<byte[]>();

using var cts = new CancellationTokenSource();
var captureTask = micSource.CaptureAsync(channel.Writer, cts.Token);

Console.WriteLine("🎤 Live transcription started. Press any key to stop.");
Console.WriteLine("───");

var transcribeTask = Task.Run(async () =>
{
    await foreach (var text in transcriptionService.TranscribeAsync(channel.Reader, cts.Token))
        Console.Write(text);
});

// --- Wait for user to stop ---
Console.ReadKey(intercept: true);
Console.WriteLine("\nStopping...");
cts.Cancel();

await Task.WhenAll(captureTask, transcribeTask);

Console.WriteLine("✅ Done.");
return 0;
