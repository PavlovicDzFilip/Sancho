using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sancho.Console.Audio;
using Sancho.Console.Logging;
using Sancho.Console.Services;
using Sancho.Console.Transcription;

namespace Sancho.Console.Orchestration;

public sealed class Orchestrator(
    MicrophoneAudioSourceFactory audioSourceFactory,
    RealtimeTranscriptionService transcriptionService,
    ClaudeService claudeService,
    Display display,
    ILogger<Orchestrator> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Select microphone (interactive prompt on clean console)
        var audioSource = audioSourceFactory.Create();

        // 2. Start the live display
        display.Start();

        // 3. Begin capture — writes "Using device" to history
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<byte[]>();

        var captureTask = audioSource.CaptureAsync(channel.Writer, cts.Token);
        var claudeTask = claudeService.RunAsync(cts.Token);

        logger.LogInformation("🎤 Live transcription + Claude assistant started.");
        logger.LogInformation("   Speak naturally. Press any key to stop.");
        logger.LogInformation("───");

        var transcribeTask = RunTranscriptionLoopAsync(
            channel.Reader.ReadAllAsync(cts.Token), cts.Token);

        // ── Wait for user to stop ─────────────────────────────────
        System.Console.ReadKey(intercept: true);
        logger.LogInformation("Stopping...");

        await cts.CancelAsync();

        await Task.WhenAll(captureTask, transcribeTask, claudeTask);

        logger.LogInformation("✅ Done.");
    }

    private async Task RunTranscriptionLoopAsync(IAsyncEnumerable<byte[]> audioInput, CancellationToken ct)
    {
        await foreach (var evt in transcriptionService.TranscribeAsync(audioInput, ct))
        {
            switch (evt)
            {
                case TranscriptionEvent.Delta delta:
                    logger.InfoInline("{0}", delta.Text);
                    break;

                case TranscriptionEvent.Completed completed:
                    logger.LogInformation(""); // finish the inline line
                    var sentence = completed.Transcript.Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                        claudeService.Enqueue(sentence);
                    break;

                case TranscriptionEvent.Error error:
                    logger.LogWarning("Transcription error: {Message}", error.Message);
                    break;
            }
        }
    }
}
