using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sancho.Console.Audio;
using Sancho.Console.Services;
using Sancho.Console.Transcription;

namespace Sancho.Console.Orchestration;

public sealed class Orchestrator(
    AudioSourceFactory audioSourceFactory,
    ITranscriptionService transcriptionService,
    TranscriptionOptions transcriptionOptions,
    ClaudeService claudeService,
    Display display,
    MicLevelMonitor micMonitor,
    ILogger<Orchestrator> logger)
{
    // Most recent ~30 s of audio is kept in the channel: a disconnect outage
    // buffers recent speech for replay without growing memory forever.
    private const int BufferedAudioSeconds = 30;
    private const int AudioChunkMilliseconds = 100; // matches the capture sources' chunk size

    private bool RecordingMode => transcriptionOptions.Mode == TranscriptionOptions.Record;

    private readonly object _bufferLock = new();
    private readonly List<string> _buffer = new();
    private bool _claudeIsReady;
    private string? _currentDelta;

    // The status bar shows the transcription state plus a live mic suffix;
    // the monitor loop re-renders it so signal loss appears without an event.
    private string _statusBase = "";
    private Display.HistoryColor _statusColor = Display.HistoryColor.Warn;
    private bool _clipWarned;

    // The idle cue rendered where the next transcribed sentence will appear:
    // it prompts the user to speak, then reassures them while audio is being
    // captured before the pause.
    private string? _hintText;
    private Display.HistoryColor _hintColor = Display.HistoryColor.Dim;

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

        // Separate from cts so a transcription failure can stop the mic
        // independently, but linked to it so shutdown cancels capture too.
        using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var captureTask = audioSource.CaptureAsync(channel.Writer, captureCts.Token);
        var claudeEvents = claudeService.RunAsync(cts.Token);

        if (RecordingMode)
        {
            display.History.AppendLine("🎤 Listening — your voice is recorded locally as a WAV file.");
            display.History.AppendLine("   Speech-to-text is not wired up yet, so Claude won't hear you.");
            display.History.AppendLine("   Flags: --continue / -c   pick a previous session to resume");
            display.History.AppendLine("   Press CTRL + C to stop and save the recording.");
        }
        else
        {
            display.History.AppendLine("🎤 Live transcription + Claude assistant started.");
            display.History.AppendLine("   Flags: --continue / -c   pick a previous session to resume");
            display.History.AppendLine("   Speak naturally. Press CTRL + C to stop.");
        }

        if (claudeService.ContinueSession)
            PrintContinuedSession();

        // Render the transcript panel immediately so the task area is visible on startup.
        display.Transcript.Clear();
        SetStatus(
            RecordingMode ? "recording: starting…" : "transcription: connecting…",
            Display.HistoryColor.Warn);

        if (!RecordingMode)
        {
            SetHint("🎤 Start talking…", Display.HistoryColor.Dim);
            UpdateTranscript();
        }

        // The ALSA mixer knows for certain whether the mic-mute switch is on
        // (the physical button toggles the Capture switch) — warn once at
        // startup; the status bar tracks the live signal from here on.
        var mutedCards = AlsaMixerCheck.FindMutedCaptureControls(logger);
        if (mutedCards.Count > 0)
        {
            var cardHint = mutedCards[0].Replace("card ", "-c ");
            display.History.AppendLine(
                $"⚠ Microphone appears muted ({string.Join(", ", mutedCards)}) — unmute with: amixer {cardHint} sset Capture cap",
                Display.HistoryColor.Warn);
        }

        var claudeTask = ConsumeClaudeEventsAsync(claudeEvents, cts.Token);
        var transcribeTask = RunTranscriptionLoopAsync(
            channel.Reader.ReadAllAsync(cts.Token), captureCts, cts.Token);
        var micTask = MonitorMicAsync(cts.Token);

        void OnCancelKeyPress(object? sender, System.ConsoleCancelEventArgs e)
        {
            // Unix delivers Ctrl+C as SIGINT rather than a ReadKey keystroke;
            // cancel the run and let the graceful shutdown finalize the file.
            e.Cancel = true;
            cts.Cancel();
        }

        System.Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            // Windows delivers Ctrl+C as a ReadKey keystroke; Unix delivers it
            // as SIGINT (CancelKeyPress). Either path cancels the run.
            var keyTask = Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        var key = System.Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) > 0)
                            return;
                    }
                }
                catch (InvalidOperationException)
                {
                    // stdin is redirected (no console) — only SIGINT can end the run now.
                }
            });

            var cancelTask = Task.Delay(Timeout.Infinite, cts.Token);
            await Task.WhenAny(keyTask, cancelTask);

            await cts.CancelAsync();
            await Task.WhenAll(captureTask, transcribeTask, claudeTask, micTask);
        }
        finally
        {
            System.Console.CancelKeyPress -= OnCancelKeyPress;
        }

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
        // WithCancellation is required: the transcription services are async
        // iterators with [EnumeratorCancellation], so their ct comes from here,
        // not from the TranscribeAsync argument. Without it, a stalled receive
        // (e.g. a silent server after a 401) can never be interrupted and
        // Ctrl+C hangs in Task.WhenAll.
        await foreach (var evt in transcriptionService.TranscribeAsync(audioInput, ct).WithCancellation(ct))
        {
            switch (evt)
            {
                case TranscriptionEvent.Recording(var path):
                    SetStatus("● recording", Display.HistoryColor.Ok);
                    display.History.AppendLine($"💾 Recording to {path}");
                    break;

                case TranscriptionEvent.Delta delta:
                    _currentDelta = (_currentDelta ?? "") + delta.Text;
                    UpdateTranscript();
                    break;

                case TranscriptionEvent.Completed completed:
                    _currentDelta = null;
                    SetHint("🎤 Start talking…", Display.HistoryColor.Dim);
                    var sentence = completed.Transcript.Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        lock (_bufferLock)
                            _buffer.Add(sentence);
                        TryFlushBuffer();
                    }

                    UpdateTranscript();
                    SetStatus("transcription: connected", Display.HistoryColor.Ok);
                    break;

                case TranscriptionEvent.SpeechDetected:
                    // Activity lives in the hint line — the status label only
                    // ever shows the connection state (connected/reconnecting/
                    // failed), so it stays untouched here.
                    SetHint("🎤 Listening — I'll transcribe when you pause", Display.HistoryColor.Ok);
                    UpdateTranscript();
                    break;

                case TranscriptionEvent.Decoding:
                    SetHint("⏳ Transcribing…", Display.HistoryColor.Ok);
                    UpdateTranscript();
                    break;

                case TranscriptionEvent.Connected:
                    SetStatus("transcription: connected", Display.HistoryColor.Ok);
                    SetHint("🎤 Start talking…", Display.HistoryColor.Dim);
                    UpdateTranscript();
                    break;

                case TranscriptionEvent.Reconnecting(var msg):
                    logger.LogWarning("Transcription reconnect: {Message}", msg);
                    SetStatus("transcription: reconnecting", Display.HistoryColor.Warn);
                    _currentDelta = null; // the server lost its partial transcript too
                    SetHint(null, Display.HistoryColor.Dim);
                    UpdateTranscript();
                    display.History.AppendLine($"⚠ {msg}");
                    break;

                case TranscriptionEvent.Failed(var msg):
                    logger.LogError("Transcription failed: {Message}", msg);
                    SetStatus(
                        RecordingMode ? "recording: failed" : "transcription: failed",
                        Display.HistoryColor.Error);
                    SetHint(null, Display.HistoryColor.Dim);
                    UpdateTranscript();
                    display.History.AppendLine($"🛑 {msg}", Display.HistoryColor.Error);
                    display.History.AppendLine(
                        RecordingMode
                            ? "   Recording stopped — restart Sancho to resume."
                            : "   Transcription stopped — restart Sancho to resume.",
                        Display.HistoryColor.Dim);
                    captureCts.Cancel();
                    break;
            }
        }
    }

    // ── Status bar ────────────────────────────────────────────────

    /// <summary>Sets the transcription-state part of the status bar; the live mic suffix is appended on render.</summary>
    private void SetStatus(string text, Display.HistoryColor color)
    {
        _statusBase = text;
        _statusColor = color;
        RenderStatus();
    }

    /// <summary>Sets the cue rendered where the next sentence will appear (null hides it).</summary>
    private void SetHint(string? text, Display.HistoryColor color)
    {
        _hintText = text;
        _hintColor = color;
    }

    /// <summary>Redraws the status bar: transcription state + live mic level.</summary>
    private void RenderStatus()
    {
        var clipped = micMonitor.IsClipped;

        // No "no signal" text: silence shows as a flat meter; clipping
        // warns once in the history above.

        if (clipped && !_clipWarned)
        {
            // One-time warning per clipping episode. A signal pinned at full
            // scale is not idle room audio — it is clipping or a broken
            // capture stage (known: AMD ACP DMIC driver bug on Ryzen AI 300).
            _clipWarned = true;
            display.History.AppendLine(
                "⚠ Microphone signal is pinned at full scale (clipped) — on Ryzen AI 300 laptops this is the known broken AMD ACP DMIC driver. Use a USB or 3.5mm headset mic and restart sancho.",
                Display.HistoryColor.Warn);
        }
        else if (!clipped)
        {
            _clipWarned = false;
        }

        // "mic:" and the meter color themselves (default/white, so the mic
        // reads as normal text); the outer color applies to the connection
        // label only, so it never changes with signal state.
        var suffix = $"{Display.HistoryColor.Default.Ansi}   mic: {LevelMeter(micMonitor.Level)}";
        display.Transcript.SetStatus(_statusBase + suffix, _statusColor);
    }

    /// <summary>Re-renders the status bar at 10 Hz so the mic meter feels live and signal loss appears without any event.</summary>
    private async Task MonitorMicAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                RenderStatus();
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Maps a 0..1 level to a battery-style ramp meter: brackets and the
    /// unfilled shell render in the default (white) foreground, the filled
    /// portion in green. Empty = all white; full = all green.
    /// </summary>
    private static string LevelMeter(double level)
    {
        const string ramp = "▁▂▃▄▅▆▇█";
        var db = 20 * Math.Log10(Math.Max(level, 1e-6));
        var t = Math.Clamp((db + 50) / 50, 0, 1);
        var filled = Math.Clamp((int)Math.Round(t * ramp.Length), 0, ramp.Length);
        var shell = ramp[filled..];

        var green = Display.HistoryColor.Ok.Ansi;
        var reset = Display.HistoryColor.Default.Ansi;
        return $"[{green}{ramp[..filled]}{reset}{shell}]{reset}";
    }

    // ── Transcript panel ───────────────────────────────────────────

    private void UpdateTranscript()
    {
        // Snapshot _buffer under lock to avoid collection-modified-during-enumeration
        // when TryFlushBuffer clears the buffer concurrently from the Claude event loop.
        string[] snapshot;
        lock (_bufferLock)
            snapshot = _buffer.ToArray();
        display.Transcript.Set(snapshot, _currentDelta, _hintText, _hintColor);
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

                UpdateTranscript(); // clears the queued lines and restores the idle hint
                claudeService.Send(combined);
                _claudeIsReady = false;
            }
        }
    }
}