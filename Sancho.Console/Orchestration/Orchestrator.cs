using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sancho.Console.Audio;
using Sancho.Console.Services;
using Sancho.Console.Transcription;
using Spectre.Console;

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
    private bool _claudeIsReady;
    private string? _currentDelta;

    public async Task RunAsync(CancellationToken ct)
    {
        var audioSource = audioSourceFactory.Create();
        display.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<byte[]>();

        var captureTask = audioSource.CaptureAsync(channel.Writer, cts.Token);
        var claudeEvents = claudeService.RunAsync(cts.Token);

        display.History.AppendLine(Markup.Escape("🎤 Live transcription + Claude assistant started."));
        display.History.AppendLine(Markup.Escape("   Speak naturally. Press any key to stop."));

        var claudeTask = ConsumeClaudeEventsAsync(claudeEvents, cts.Token);
        var transcribeTask = RunTranscriptionLoopAsync(
            channel.Reader.ReadAllAsync(cts.Token), cts.Token);

        // Escape to stop, arrow keys scroll the history panel
        while (true)
        {
            var key = System.Console.ReadKey(intercept: true);
            if (!display.HandleScrollKey(key))
                break;
        }
        display.History.AppendLine(Markup.Escape("Stopping..."));

        await cts.CancelAsync();
        await Task.WhenAll(captureTask, transcribeTask, claudeTask);

        display.History.AppendLine(Markup.Escape("✅ Done."));
    }

    // ── Claude event consumer ──────────────────────────────────────

    private async Task ConsumeClaudeEventsAsync(
        IAsyncEnumerable<ClaudeEvent> events, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in events.WithCancellation(ct))
            {
                switch (evt)
                {
                    case ClaudeEvent.Ready:
                        lock (_bufferLock)
                            _claudeIsReady = true;
                        TryFlushBuffer();
                        break;

                    case ClaudeEvent.TurnStart:
                        display.History.AppendLine("[#FF8C00]🤖 ");
                        break;

                    case ClaudeEvent.AssistantText(var text):
                        display.History.AppendInline(Markup.Escape(text));
                        break;

                    case ClaudeEvent.ToolUse(var name, var preview):
                        display.History.AppendInline("[/]");
                        display.History.FinishLine();
                        display.History.AppendLine(
                            $"[dim]  🔧 {Markup.Escape(name)}: {Markup.Escape(preview)}[/]");
                        break;

                    case ClaudeEvent.ToolResult(var toolId, var isError):
                        display.History.AppendLine(
                            $"[dim]  [tool {toolId}… {(isError ? "✗" : "✓")}][/]");
                        break;

                    case ClaudeEvent.TurnComplete:
                        display.History.AppendInline("[/]");
                        display.History.FinishLine();
                        break;

                    case ClaudeEvent.Status(var msg, _):
                        display.History.AppendLine(Markup.Escape(msg));
                        break;

                    case ClaudeEvent.Error(var msg):
                        logger.LogError("Claude error: {Msg}", msg);
                        display.History.AppendLine(Markup.Escape($"⚠ {msg}"));
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
                    _currentDelta = (_currentDelta ?? "") + delta.Text;
                    UpdateTranscript();
                    break;

                case TranscriptionEvent.Completed completed:
                    _currentDelta = null;
                    var sentence = completed.Transcript.Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        lock (_bufferLock)
                            _buffer.Add(sentence);
                        UpdateTranscript();
                        TryFlushBuffer();
                    }

                    break;

                case TranscriptionEvent.Error error:
                    logger.LogWarning("Transcription error: {Message}", error.Message);
                    break;
            }
        }
    }

    // ── Transcript panel ───────────────────────────────────────────

    private void UpdateTranscript()
    {
        display.Transcript.Set(_buffer, _currentDelta);
    }

    // ── Buffer flush ───────────────────────────────────────────────

    private void TryFlushBuffer()
    {
        string? combined;
        lock (_bufferLock)
        {
            if (!_claudeIsReady)
                return;

            combined = string.Join("\n", _buffer);
            _buffer.Clear();
            if (!string.IsNullOrEmpty(combined))
            {
                foreach (var line in combined.Split('\n'))
                    display.History.AppendLine($"[bold]👤 {Markup.Escape(line)}[/]");

                display.Transcript.Clear();
                claudeService.Send(combined);
                _claudeIsReady = false;
            }
        }
    }
}