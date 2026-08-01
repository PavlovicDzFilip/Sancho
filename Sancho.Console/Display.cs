using System.Text;

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
        public static readonly HistoryColor Default = new("");
        public static readonly HistoryColor User = new("\e[1m");
        public static readonly HistoryColor Claude = new("\e[38;5;214m");
        public static readonly HistoryColor Dim = new("\e[38;5;240m");
        public string Ansi { get; }
        private HistoryColor(string ansi) => Ansi = ansi;
    }

    public Display()
    {
        History = new HistoryPanel();
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

    internal static string FormatLine(string text, HistoryColor? color = null)
    {
        var c = color ?? HistoryColor.Default;
        var reset = c.Ansi.Length > 0 ? "\e[0m" : "";
        return $"{c.Ansi}{text}{reset}";
    }

    // ── Nested panels ────────────────────────────────────────────

    /// <summary>Top panel — scrolls naturally.</summary>
    public sealed class HistoryPanel
    {
        private readonly StringBuilder _currentLine = new();

        internal HistoryPanel()
        {
        }

        /// <summary>Write a complete line to the console.</summary>
        public void AppendLine(string text, HistoryColor? color = null)
        {
            FinishLine();
            System.Console.WriteLine(FormatLine(text, color));
        }

        /// <summary>Write streaming text to the current line. Overwrites with \r.</summary>
        public void AppendInline(string text, HistoryColor? color = null)
        {
            var formatted = FormatLine(text, color);
            _currentLine.Clear();
            _currentLine.Append(formatted);
            System.Console.Write($"\r{formatted}\e[0K"); // clear to end of line
        }

        /// <summary>End the current inline line and move to the next.</summary>
        public void FinishLine()
        {
            if (_currentLine.Length > 0)
            {
                System.Console.Write("\r\e[0K"); // clear the inline line
                System.Console.WriteLine();
                _currentLine.Clear();
            }
        }
    }

    /// <summary>Bottom panel — pinned transcript.</summary>
    public sealed class TranscriptPanel
    {
        private readonly Display _display;

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

                // Separator
                System.Console.SetCursorPosition(0, startRow);
                System.Console.Write(new string('─', width));

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