using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sancho.Console.Audio;
using Sancho.Console.Services;
using Sancho.Console.Transcription;

namespace Sancho.Console.Orchestration;

public sealed class Orchestrator(
    MicrophoneAudioSourceFactory audioSourceFactory,
    ITranscriptionService transcriptionService,
    ClaudeService claudeService,
    Display display,
    ILogger<Orchestrator> logger)
{
    // Most recent ~30 s of audio is kept in the channel: a disconnect outage
    // buffers recent speech for replay without growing memory forever.
    private const int BufferedAudioSeconds = 30;
    private const int AudioChunkMilliseconds = 100; // matches MicrophoneAudioSource.BufferMilliseconds

    private readonly object _bufferLock = new();
    private readonly List<string> _buffer = new();
    private bool _claudeIsReady;
    private string? _currentDelta;

    public async Task RunAsync(CancellationToken ct)
    {
        display.Start();
        var audioSource = audioSourceFactory.Create();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(
            BufferedAudioSeconds * 1000 / AudioChunkMilliseconds)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

        using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var captureTask = audioSource.CaptureAsync(channel.Writer, captureCts.Token);
        var claudeEvents = claudeService.RunAsync(cts.Token);

        display.History.AppendLine("🎤 Live transcription + Claude assistant started.");
        display.History.AppendLine("   Flags: --continue / -c   pick a previous session to resume");
        display.History.AppendLine("   Speak naturally. Press CTRL + C to stop.");

        if (claudeService.ContinueSession)
            PrintContinuedSession();

        // Render the transcript panel immediately so the task area is visible on startup.
        display.Transcript.Clear();
        display.Transcript.SetStatus("transcription: connecting…", Display.HistoryColor.Warn);

        var claudeTask = ConsumeClaudeEventsAsync(claudeEvents, cts.Token);
        var transcribeTask = RunTranscriptionLoopAsync(
            channel.Reader.ReadAllAsync(cts.Token), captureCts, cts.Token);

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
        // Show the full previous session, in order, untruncated.
        var messages = claudeService.GetSessionMessages();
        if (messages.Count == 0)
        {
            display.History.AppendLine("   (no prior session found)", Display.HistoryColor.Dim);
            return;
        }

        display.History.AppendLine("── Continuing last session ──", Display.HistoryColor.Dim);
        foreach (var (isUser, text) in messages)
        {
            if (isUser)
                display.History.AppendLine($"💬 {text}");
            else
                display.History.AppendLine($"🤖 {text}", Display.HistoryColor.Claude);
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

                    case ClaudeEvent.Status(var msg, _) when msg.StartsWith(
                        "[claude-code:unrecognized_model]", StringComparison.Ordinal):
                        // Claude Code's model-registry warning when a third-party backend
                        // model id (e.g. DeepSeek) is configured — benign, keep it out of
                        // the transcript but still available at Debug verbosity.
                        logger.LogDebug("Ignored claude stderr diagnostic: {Msg}", msg);
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
        IAsyncEnumerable<byte[]> audioInput, CancellationTokenSource captureCts, CancellationToken ct)
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

                case TranscriptionEvent.Connected:
                    display.Transcript.SetStatus("transcription: connected", Display.HistoryColor.Ok);
                    break;

                case TranscriptionEvent.Reconnecting(var msg):
                    logger.LogWarning("Transcription reconnect: {Message}", msg);
                    display.Transcript.SetStatus("transcription: reconnecting", Display.HistoryColor.Warn);
                    _currentDelta = null; // the server lost its partial transcript too
                    UpdateTranscript();
                    display.History.AppendLine($"⚠ {msg}");
                    break;

                case TranscriptionEvent.Failed(var msg):
                    logger.LogError("Transcription failed: {Message}", msg);
                    display.Transcript.SetStatus("transcription: failed", Display.HistoryColor.Error);
                    display.History.AppendLine($"🛑 {msg}", Display.HistoryColor.Error);
                    display.History.AppendLine("   Transcription stopped — restart Sancho to resume.", Display.HistoryColor.Dim);
                    captureCts.Cancel();
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