using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sancho;
using Sancho.Console;

// --- Build configuration ---
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{environment}.json", optional: true)
    .Build();

// --- Set up DI ---
var services = new ServiceCollection();

services.AddSingleton<IConfiguration>(config);
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

// Options: bind from JSON, validate eagerly
services.AddOptions<TranscriptionOptions>()
    .Bind(config.GetSection("Transcription"))
    .Validate(opt => !string.IsNullOrWhiteSpace(opt.ApiKey),
        "OpenAI API key is required. Set 'Transcription:ApiKey' in appsettings.json.")
    .ValidateOnStart();

services.AddSingleton<RealtimeTranscriptionService>();
services.AddSingleton<MicrophoneAudioSource>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MicrophoneAudioSource>>();
    return MicrophoneAudioSource.Create(logger);
});

var provider = services.BuildServiceProvider();

// ValidateOnStart — throws here if ApiKey is missing
var transcriptionService = provider.GetRequiredService<RealtimeTranscriptionService>();
using var micSource = provider.GetRequiredService<MicrophoneAudioSource>();

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
