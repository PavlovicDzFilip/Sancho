namespace Sancho.Console;

/// <summary>
/// Two-panel display: History scrolls naturally in the console,
/// Transcript is pinned at the bottom via ANSI scroll region.
/// </summary>
public sealed class Display : IDisposable
{
    // ── Transcript geometry ───────────────────────────────────────
    private const int TranscriptRows = 5; // fixed: 1 separator + up to 4 content rows
    private readonly object _renderLock = new();

    // ── Value objects ─────────────────────────────────────────────

    public sealed class HistoryColor
    {
        public static readonly HistoryColor Default = new("\e[0m");
        public static readonly HistoryColor Claude = new("\e[38;5;214m");
        public static readonly HistoryColor Dim = new("\e[38;5;240m");
        public static readonly HistoryColor Error = new("\e[1;31m"); // bold red — fatal failures
        public static readonly HistoryColor Ok = new("\e[32m");       // green — healthy state
        public static readonly HistoryColor Warn = new("\e[33m");     // yellow — transient issues
        public string Ansi { get; }
        private HistoryColor(string ansi) => Ansi = ansi;
    }

    public Display()
    {
        History = new HistoryPanel(this);
        Transcript = new TranscriptPanel(this);
    }

    public HistoryPanel History { get; }
    public TranscriptPanel Transcript { get; }

    public void Start()
    {
        // Reserve bottom 5 rows via ANSI scroll region — scroll region
        // ends 5 rows above the bottom so history never overlaps transcript.
        var total = System.Console.WindowHeight;
        if (total > TranscriptRows)
            System.Console.Write($"\e[1;{total - TranscriptRows}r");
        System.Console.Clear();
    }

    /// <summary>Re-apply the scroll region (e.g. after terminal resize).</summary>
    internal void RefreshScrollRegion()
    {
        var total = System.Console.WindowHeight;
        if (total <= TranscriptRows) return;
        System.Console.Write($"\e[r"); // reset first
        System.Console.Write($"\e[1;{total - TranscriptRows}r");
    }

    public void Dispose()
    {
        System.Console.Write("\e[r"); // reset scroll region
    }

    // ── ANSI helpers ──────────────────────────────────────────────

    internal static string FormatLine(string text, HistoryColor color)
    {
        return $"{color.Ansi}{text}{HistoryColor.Default.Ansi}";
    }

    // ── Nested panels ────────────────────────────────────────────

    /// <summary>Top panel — scrolls naturally.</summary>
    public sealed class HistoryPanel
    {
        private readonly Display _display;

        internal HistoryPanel(Display display) => _display = display;

        /// <summary>Write a complete line to the console.</summary>
        public void AppendLine(string text, HistoryColor? color = null)
        {
            color ??= HistoryColor.Default;
            lock (_display._renderLock)
            {
                System.Console.WriteLine(FormatLine(text, color));
            }
        }
    }

    /// <summary>Bottom panel — pinned transcript.</summary>
    public sealed class TranscriptPanel
    {
        private readonly Display _display;
        private string? _statusText;
        private HistoryColor? _statusColor;

        internal TranscriptPanel(Display display) => _display = display;

        public void Set(IReadOnlyList<string> queued, string? currentDelta)
        {
            lock (_display._renderLock)
            {
                var (left, top) = System.Console.GetCursorPosition();
                var total = System.Console.WindowHeight;
                var width = System.Console.WindowWidth;

                // Build wrapped content lines
                var lines = new List<string>();
                foreach (var q in queued)
                    lines.AddRange(Wrap($"  ⏳ {q}", width));
                if (currentDelta is { Length: > 0 })
                    lines.AddRange(Wrap($"  ▶ {currentDelta}", width));

                // Fixed 5-row transcript: separator + up to 4 content rows
                const int maxContentRows = TranscriptRows - 1; // 4
                var contentRows = Math.Min(lines.Count, maxContentRows);

                var startRow = Math.Max(0, total - TranscriptRows);

                // Re-apply scroll region (handles terminal resize)
                _display.RefreshScrollRegion();

                // Clear the entire transcript area
                for (var i = 0; i < TranscriptRows; i++)
                {
                    System.Console.SetCursorPosition(0, startRow + i);
                    System.Console.Write(new string(' ', width));
                }

                // Separator (shows the connection status when set)
                DrawSeparator();

                // Content (bottom-up) — only the most recent lines fit
                var row = startRow + TranscriptRows - 1;
                for (var i = contentRows - 1; i >= 0; i--, row--)
                {
                    System.Console.SetCursorPosition(0, row);
                    System.Console.Write(lines[lines.Count - contentRows + i]);
                }

                // Restore cursor
                System.Console.SetCursorPosition(left, top);
            }
        }

        /// <summary>Updates the connection status shown on the separator line.</summary>
        public void SetStatus(string text, HistoryColor color)
        {
            lock (_display._renderLock)
            {
                _statusText = text;
                _statusColor = color;
                DrawSeparator();
            }
        }

        /// <summary>Draws the separator line, with the status centered when set.</summary>
        private void DrawSeparator()
        {
            var total = System.Console.WindowHeight;
            var width = System.Console.WindowWidth;
            var startRow = Math.Max(0, total - TranscriptRows);
            var (left, top) = System.Console.GetCursorPosition();

            System.Console.SetCursorPosition(0, startRow);

            var label = _statusText is null ? "" : $" {_statusText} ";
            if (label.Length == 0)
            {
                System.Console.Write(new string('─', width));
            }
            else
            {
                var leftWidth = Math.Max(0, (width - label.Length) / 2);
                var rightWidth = Math.Max(0, width - leftWidth - label.Length);
                System.Console.Write(new string('─', leftWidth));
                System.Console.Write(FormatLine(label, _statusColor!));
                System.Console.Write(new string('─', rightWidth));
            }

            System.Console.SetCursorPosition(left, top);
        }

        public void Clear()
        {
            Set(Array.Empty<string>(), null);
        }

        private static List<string> Wrap(string text, int width)
        {
            var result = new List<string>();
            if (width <= 0)
            {
                result.Add(text);
                return result;
            }

            var remaining = text.AsSpan();
            while (remaining.Length > 0)
            {
                var take = Math.Min(remaining.Length, width);
                result.Add(remaining[..take].ToString());
                remaining = remaining[take..];
            }

            return result;
        }
    }
}