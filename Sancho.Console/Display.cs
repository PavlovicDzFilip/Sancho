using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Sancho.Console;

/// <summary>
/// Two-panel live display: History (top) for the conversation log,
/// Transcript (bottom) for buffered sentences awaiting Claude.
/// </summary>
public sealed class Display : IDisposable
{
    private readonly Layout _layout;
    private readonly Layout _historyLayout;
    private readonly Layout _transcriptLayout;

    private LiveDisplayContext? _ctx;
    private LiveDisplay? _live;
    private readonly ManualResetEventSlim _stopped = new();

    // ── Scroll state ──────────────────────────────────────────────
    private string _lastHistoryText = "";
    private int? _firstVisibleLine; // null = auto-scroll to tail

    public Display()
    {
        _historyLayout = new Layout("History");
        _transcriptLayout = new Layout("Transcript");

        _historyLayout.Update(Panel("History", ""));
        _transcriptLayout.Update(Panel("Transcript", ""));

        _layout = new Layout("Root")
            .SplitRows(_historyLayout, _transcriptLayout);

        _historyLayout.Ratio(2);
        _transcriptLayout.MinimumSize(3);

        History = new HistoryPanel(this);
        Transcript = new TranscriptPanel(this);
    }

    public HistoryPanel History { get; }
    public TranscriptPanel Transcript { get; }

    public void Start()
    {
        _live = AnsiConsole.Live(_layout);
        Task.Run(() => _live.Start(ctx =>
        {
            _ctx = ctx;
            ctx.Refresh();
            _stopped.Wait();
        }));
    }

    public void Dispose()
    {
        _stopped.Set();

        // Give the LiveDisplay a moment to stop and restore the terminal.
        // Dispose is called after all tasks complete, so a brief spin is safe.
        for (var i = 0; i < 10 && _ctx is not null; i++)
            Thread.Sleep(50);

        _stopped.Dispose();

        // Dump full history to the terminal so it lands in native scrollback.
        var full = History.GetFullText();
        if (!string.IsNullOrWhiteSpace(full))
            AnsiConsole.Write(new Markup(full + "\n"));
    }

    // ── Internal refresh ───────────────────────────────────────────

    internal void RefreshHistory(string markupText)
    {
        _lastHistoryText = markupText;

        var header = "History";
        var maxLines = Math.Max(5, System.Console.WindowHeight * 2 / 3 - 3);
        var allLines = markupText.Replace("\r", "").Split('\n');

        string visible;
        if (allLines.Length <= maxLines)
        {
            visible = markupText;
        }
        else
        {
            var tailStart = allLines.Length - maxLines;
            var start = _firstVisibleLine.HasValue
                ? Math.Clamp(_firstVisibleLine.Value, 0, allLines.Length - maxLines)
                : tailStart;

            // If scrolled and caught up to the tail, snap back to auto
            if (_firstVisibleLine.HasValue && start >= tailStart)
            {
                _firstVisibleLine = null;
                start = tailStart;
            }

            visible = string.Join("\n", allLines[start..(start + maxLines)]);

            if (_firstVisibleLine.HasValue)
                header = $"History ↑{allLines.Length - maxLines - start}";
        }

        var content = string.IsNullOrEmpty(visible)
            ? (IRenderable)new Text("")
            : new Markup(visible);
        _historyLayout.Update(new Panel(content).Header(header).Expand());
        _ctx?.Refresh();
    }

    private void RerenderHistory()
    {
        if (_lastHistoryText.Length > 0)
            RefreshHistory(_lastHistoryText);
    }

    internal void RefreshTranscript(string markupText)
    {
        var lineCount = Math.Max(1, markupText.Count(c => c == '\n') + 1);
        _transcriptLayout.MinimumSize(Math.Max(3, lineCount + 2));
        var content = string.IsNullOrEmpty(markupText)
            ? (IRenderable)new Text("")
            : new Markup(markupText);
        _transcriptLayout.Update(new Panel(content)
            .Header("Transcript")
            .Expand());
        _ctx?.Refresh();
    }

    private static Panel Panel(string header, string text)
    {
        return new Panel(string.IsNullOrEmpty(text) ? "" : Markup.Escape(text))
            .Header(header)
            .Expand();
    }

    // ── Scroll keys ───────────────────────────────────────────────

    /// <summary>Handle a scroll key. Returns true if the key was handled.</summary>
    public bool HandleScrollKey(ConsoleKeyInfo key)
    {
        var maxLines = Math.Max(5, System.Console.WindowHeight * 2 / 3 - 3);
        var allLines = _lastHistoryText.Replace("\r", "").Split('\n');
        var tailStart = Math.Max(0, allLines.Length - maxLines);

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.PageUp:
            {
                var cur = _firstVisibleLine ?? tailStart;
                _firstVisibleLine = Math.Max(0, cur - 1);
                RerenderHistory();
                return true;
            }
            case ConsoleKey.DownArrow:
            case ConsoleKey.PageDown:
            {
                var cur = _firstVisibleLine ?? tailStart;
                if (cur + 1 >= tailStart)
                    _firstVisibleLine = null;
                else
                    _firstVisibleLine = cur + 1;
                RerenderHistory();
                return true;
            }
            case ConsoleKey.End:
                _firstVisibleLine = null;
                RerenderHistory();
                return true;
        }

        return false;
    }

    // ── Nested panels ──────────────────────────────────────────────

    /// <summary>Top panel — full conversation log.</summary>
    public sealed class HistoryPanel
    {
        private readonly Display _display;
        private readonly StringBuilder _lines = new();

        internal HistoryPanel(Display display) => _display = display;

        /// <summary>Append a complete line to the history (escaped — safe for plain text).</summary>
        public void AppendLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            _lines.AppendLine(Markup.Escape(text));
            _display.RefreshHistory(_lines.ToString());
        }

        /// <summary>Append a pre-formatted markup line. Caller must balance tags.</summary>
        public void AppendMarkup(string markup)
        {
            if (string.IsNullOrWhiteSpace(markup))
                return;
            _lines.AppendLine(markup);
            _display.RefreshHistory(_lines.ToString());
        }

        /// <summary>Append text to the current line (escaped).</summary>
        public void AppendInline(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            _lines.Append(Markup.Escape(text));
            _display.RefreshHistory(_lines.ToString());
        }

        /// <summary>Append markup to the current line. Caller must balance tags.</summary>
        public void AppendInlineMarkup(string markup)
        {
            if (string.IsNullOrWhiteSpace(markup))
                return;
            _lines.Append(markup);
            _display.RefreshHistory(_lines.ToString());
        }

        /// <summary>End the current line with a newline if needed.</summary>
        public void FinishLine()
        {
            if (_lines.Length > 0 && _lines[^1] != '\n')
            {
                _lines.AppendLine();
                _display.RefreshHistory(_lines.ToString());
            }
        }

        /// <summary>Return the full, untruncated history.</summary>
        internal string GetFullText() => _lines.ToString();
    }

    /// <summary>Bottom panel — queued sentences awaiting Claude.</summary>
    public sealed class TranscriptPanel
    {
        private readonly Display _display;

        internal TranscriptPanel(Display display) => _display = display;

        /// <summary>
        /// Replace the transcript content.
        /// <paramref name="queued"/> are completed sentences waiting to be sent
        /// (shown dimmed with a clock). <paramref name="currentDelta"/> is the
        /// live in-progress transcription (shown bright with a play marker).
        /// </summary>
        public void Set(IReadOnlyList<string> queued, string? currentDelta)
        {
            var sb = new StringBuilder();

            foreach (var line in queued)
                sb.Append("[dim]⏳ ").Append(Markup.Escape(line)).AppendLine("[/]");

            if (currentDelta is { Length: > 0 })
                sb.Append("▶ ").Append(Markup.Escape(currentDelta));

            _display.RefreshTranscript(sb.ToString());
        }

        /// <summary>Clear the transcript panel.</summary>
        public void Clear()
        {
            _display.RefreshTranscript("");
        }
    }
}
