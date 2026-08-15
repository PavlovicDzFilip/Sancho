using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sancho.Console.Audio;
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
    private bool _claudeIsReady;
    private string? _currentDelta;

    public async Task RunAsync(CancellationToken ct)
    {
        display.Start();
        var audioSource = audioSourceFactory.Create();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<byte[]>();

        var captureTask = audioSource.CaptureAsync(channel.Writer, cts.Token);
        var claudeEvents = claudeService.RunAsync(cts.Token);

        display.History.AppendLine("🎤 Live transcription + Claude assistant started.");
        display.History.AppendLine("   Speak naturally. Press CTRL + C to stop.");

        if (claudeService.ContinueSession)
            PrintContinuedSession();

        // Render the transcript panel immediately so the task area is visible on startup.
        display.Transcript.Clear();

        var claudeTask = ConsumeClaudeEventsAsync(claudeEvents, cts.Token);
        var transcribeTask = RunTranscriptionLoopAsync(
            channel.Reader.ReadAllAsync(cts.Token), cts.Token);

        ConsoleKeyInfo pressedKey;
        bool shouldStop;
        do
        {
            pressedKey = System.Console.ReadKey(intercept: true);
            shouldStop = pressedKey.Key == ConsoleKey.C && (pressedKey.Modifiers & ConsoleModifiers.Control) > 0;
        } while (!shouldStop);

        await cts.CancelAsync();
        await Task.WhenAll(captureTask, transcribeTask, claudeTask);

        display.History.AppendLine("✅ Done.");
    }

    private void PrintContinuedSession()
    {
        const int maxMessages = 10;
        const int maxLength = 300;

        var messages = claudeService.GetLastSessionMessages(maxMessages);
        if (messages.Count == 0)
        {
            display.History.AppendLine("   (no prior session found)", Display.HistoryColor.Dim);
            return;
        }

        display.History.AppendLine("── Continuing last session ──", Display.HistoryColor.Dim);
        foreach (var (isUser, text) in messages)
        {
            var shown = text.Length > maxLength ? text[..maxLength] + "…" : text;
            if (isUser)
                display.History.AppendLine($"💬 {shown}");
            else
                display.History.AppendLine($"🤖 {shown}", Display.HistoryColor.Claude);
        }
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
                        display.History.AppendLine("🤖", Display.HistoryColor.Claude);
                        break;

                    case ClaudeEvent.AssistantText(var text):
                        display.History.AppendLine(text, color: Display.HistoryColor.Claude);
                        break;

                    case ClaudeEvent.ToolUse(var name, var preview):
                        display.History.AppendLine($"🔧 {name}: {preview}", Display.HistoryColor.Dim);
                        break;

                    case ClaudeEvent.ToolResult(var toolId, var isError):
                        display.History.AppendLine(
                            $"tool {toolId}… {(isError ? "✗" : "✓")}",
                            color: Display.HistoryColor.Dim);
                        break;

                    case ClaudeEvent.TurnComplete:
                        break;

                    case ClaudeEvent.Status(var msg, _):
                        display.History.AppendLine(msg);
                        break;

                    case ClaudeEvent.Error(var msg):
                        logger.LogError("Claude error: {Msg}", msg);
                        display.History.AppendLine($"⚠ {msg}");
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
                    display.History.AppendLine($"⚠ {error.Message}");
                    break;
            }
        }
    }

    // ── Transcript panel ───────────────────────────────────────────

    private void UpdateTranscript()
    {
        // Snapshot _buffer under lock to avoid collection-modified-during-enumeration
        // when TryFlushBuffer clears the buffer concurrently from the Claude event loop.
        string[] snapshot;
        lock (_bufferLock)
            snapshot = _buffer.ToArray();
        display.Transcript.Set(snapshot, _currentDelta);
    }

    // ── Buffer flush ───────────────────────────────────────────────

    private void TryFlushBuffer()
    {
        string? combined;
        lock (_bufferLock)
        {
            if (!_claudeIsReady)
                return;

            combined = string.Join(Environment.NewLine, _buffer);
            _buffer.Clear();
            if (!string.IsNullOrEmpty(combined))
            {
                foreach (var line in combined.Split(Environment.NewLine))
                    display.History.AppendLine($"💬 {line}");

                display.Transcript.Clear();
                claudeService.Send(combined);
                _claudeIsReady = false;
            }
        }
    }
}