using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sancho.Audio;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole().SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger<MicrophoneAudioSource>();
using var source = MicrophoneAudioSource.Create(logger); // auto-pick or interactive selector

var channel = Channel.CreateUnbounded<byte[]>();
using var cts = new CancellationTokenSource();

var captureTask = source.CaptureAsync(channel.Writer, cts.Token);

// Consume in the background and tally bytes
long totalBytes = 0;
var readTask = Task.Run(async () =>
{
    await foreach (var chunk in channel.Reader.ReadAllAsync())
        totalBytes += chunk.Length;
});

Console.WriteLine("🎤 Recording... press any key to stop.");
Console.ReadKey(intercept: true);

Console.WriteLine("Stopping...");
cts.Cancel();
await captureTask;
channel.Writer.Complete();
await readTask;

Console.WriteLine(
    $"✅ Done. Captured {totalBytes:N0} bytes " +
    $"({totalBytes / 2:N0} samples, " +
    $"{totalBytes / 88200.0:F1} seconds of mono 16-bit PCM @ 44100 Hz).");

return 0;
