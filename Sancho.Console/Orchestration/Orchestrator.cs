using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sancho.Console.Audio;
using Sancho.Console.Logging;
using Sancho.Console.Services;
using Sancho.Console.Transcription;

namespace Sancho.Console.Orchestration;

public sealed class Orchestrator(
    IAudioSource audioSource,
    RealtimeTranscriptionService transcriptionService,
    ClaudeService claudeService,
    ILogger<Orchestrator> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<byte[]>();

        var captureTask = audioSource.CaptureAsync(channel.Writer, cts.Token);
        var claudeTask = claudeService.RunAsync(cts.Token);

        logger.LogInformation("🎤 Live transcription + Claude assistant started.");
        logger.LogInformation("   Speak naturally. Press any key to stop.");
        logger.LogInformation("───");

        var transcribeTask = RunTranscriptionLoopAsync(channel.Reader, cts.Token);

        // ── Wait for user to stop ─────────────────────────────────
        System.Console.ReadKey(intercept: true);
        logger.LogInformation("Stopping...");

        await cts.CancelAsync();

        await Task.WhenAll(captureTask, transcribeTask, claudeTask);

        logger.LogInformation("✅ Done.");
    }

    private async Task RunTranscriptionLoopAsync(ChannelReader<byte[]> audioInput, CancellationToken ct)
    {
        await foreach (var chunk in transcriptionService.TranscribeAsync(audioInput, ct))
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
    }
}
