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
    private readonly object _bufferLock = new();
    private readonly List<string> _buffer = new();
    private bool _claudeIsReady = false;

    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Select microphone
        var audioSource = audioSourceFactory.Create();

        // 2. Start the live display
        display.Start();

        // 3. Begin capture
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<byte[]>();

        var captureTask = audioSource.CaptureAsync(channel.Writer, cts.Token);

        // 4. Start Claude — returns an event stream, lazy until enumerated
        var claudeEvents = claudeService.RunAsync(cts.Token);

        logger.LogInformation("🎤 Live transcription + Claude assistant started.");
        logger.LogInformation("   Speak naturally. Press any key to stop.");
        logger.LogInformation("───");

        // 5. Consume Claude events in background
        var claudeTask = ConsumeClaudeEventsAsync(claudeEvents, cts.Token);

        // 6. Transcription loop
        var transcribeTask = RunTranscriptionLoopAsync(
            channel.Reader.ReadAllAsync(cts.Token), cts.Token);

        // ── Wait for user to stop ─────────────────────────────────
        System.Console.ReadKey(intercept: true);
        logger.LogInformation("Stopping...");

        await cts.CancelAsync();
        await Task.WhenAll(captureTask, transcribeTask, claudeTask);

        logger.LogInformation("✅ Done.");
    }

    // ── Claude event consumer ──────────────────────────────────────

    private async Task ConsumeClaudeEventsAsync(
        IAsyncEnumerable<ClaudeEvent> events, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in events.WithCancellation(ct))
            {
                logger.LogDebug($"Received event: {evt.GetType().Name}");
                switch (evt)
                {
                    case ClaudeEvent.Ready:
                        lock (_bufferLock)
                        {
                            _claudeIsReady = true;
                        }

                        TryFlushBuffer();
                        break;

                    case ClaudeEvent.TurnStart:
                        logger.LogInformation("───");
                        break;

                    case ClaudeEvent.AssistantText(var text):
                        logger.InfoInline("{0}", text);
                        break;

                    case ClaudeEvent.ToolUse(var name, var preview):
                        logger.LogInformation(""); // finish inline text
                        logger.LogInformation("  [{Name}: {Preview}]", name, preview);
                        break;

                    case ClaudeEvent.ToolResult(var toolId, var isError):
                        logger.LogInformation("  [tool {Id}… {Status}]",
                            toolId, isError ? "✗" : "✓");
                        break;

                    case ClaudeEvent.TurnComplete:
                        logger.LogInformation(""); // finish inline
                        break;

                    case ClaudeEvent.Status(var msg, _):
                        logger.LogInformation("{Msg}", msg);
                        break;

                    case ClaudeEvent.Error(var msg):
                        logger.LogError("Claude error: {Msg}", msg);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Transcription loop ─────────────────────────────────────────

    private async Task RunTranscriptionLoopAsync(
        IAsyncEnumerable<byte[]> audioInput, CancellationToken ct)
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
                    {
                        lock (_bufferLock)
                        {
                            _buffer.Add(sentence);
                        }

                        TryFlushBuffer();
                    }

                    break;

                case TranscriptionEvent.Error error:
                    logger.LogWarning("Transcription error: {Message}", error.Message);
                    break;
            }
        }
    }

    // ── Buffer flush ───────────────────────────────────────────────

    private void TryFlushBuffer()
    {
        lock (_bufferLock)
        {
            if (!_claudeIsReady)
            {
                logger.LogDebug("AI agent is not ready, not flushing messages.");
                return;
            }

            var combined = string.Join(Environment.NewLine, _buffer);
            _buffer.Clear();
            if (!string.IsNullOrEmpty(combined))
            {
                logger.LogDebug("Sending prompt");
                claudeService.Send(combined);
            }
        }
    }
}