using System.Text;

namespace Sancho.Console;

/// <summary>
/// Two-panel display: History scrolls naturally in the console,
/// Transcript is pinned at the bottom via ANSI scroll region.
/// </summary>
public sealed class Display : IDisposable
{
    // ── Transcript geometry ───────────────────────────────────────
    private int _transcriptRows = 1; // starts at 1 (separator only), grows as needed
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

    public readonly record struct HistoryLine(
        string Text, HistoryColor? Color = null);

    public Display()
    {
        History = new HistoryPanel(this);
        Transcript = new TranscriptPanel(this);
    }

    public HistoryPanel History { get; }
    public TranscriptPanel Transcript { get; }

    public void Start()
    {
        // Reserve bottom rows via ANSI scroll region
        var total = System.Console.WindowHeight;
        if (total > _transcriptRows)
            System.Console.Write($"\e[1;{total - _transcriptRows}r");
        System.Console.Clear();
    }

    /// <summary>Update the transcript panel height, adjusting the ANSI scroll region.</summary>
    internal void UpdateTranscriptRows(int rows)
    {
        if (rows <= _transcriptRows) return; // only grow, never shrink
        var total = System.Console.WindowHeight;
        if (total <= rows) return;
        System.Console.Write($"\e[r"); // reset first
        System.Console.Write($"\e[1;{total - rows}r");
        _transcriptRows = rows;
    }

    public void Dispose()
    {
        System.Console.Write("\e[r"); // reset scroll region
    }

    // ── ANSI helpers ──────────────────────────────────────────────

    internal static string FormatLine(HistoryLine line)
    {
        var color = line.Color ?? HistoryColor.Default;
        var reset = color.Ansi.Length > 0 ? "\e[0m" : "";
        return $"{color.Ansi}{line.Text}{reset}";
    }

    // ── Nested panels ────────────────────────────────────────────

    /// <summary>Top panel — scrolls naturally.</summary>
    public sealed class HistoryPanel
    {
        private readonly Display _display;
        private readonly StringBuilder _currentLine = new();

        internal HistoryPanel(Display display) => _display = display;

        /// <summary>Write a complete line to the console.</summary>
        public void AppendLine(HistoryLine line)
        {
            FinishLine();
            System.Console.WriteLine(FormatLine(line));
        }

        /// <summary>Write streaming text to the current line. Overwrites with \r.</summary>
        public void AppendInline(HistoryLine line)
        {
            var formatted = FormatLine(line);
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

                // Calculate needed rows dynamically — no cap, grows as needed
                var neededRows = 1 + lines.Count; // 1 for separator
                if (neededRows > total) neededRows = total; // don't exceed terminal height

                // Only grow, never shrink — avoids orphaned rows in the scroll region
                _display.UpdateTranscriptRows(neededRows);

                var allocatedRows = _display._transcriptRows;
                var startRow = Math.Max(0, total - allocatedRows);

                // Clear the entire allocated transcript area
                for (var i = 0; i < allocatedRows; i++)
                {
                    System.Console.SetCursorPosition(0, startRow + i);
                    System.Console.Write(new string(' ', width));
                }

                // Separator
                System.Console.SetCursorPosition(0, startRow);
                System.Console.Write(new string('─', width));

                // Content (bottom-up)
                var row = startRow + allocatedRows - 1;
                for (var i = lines.Count - 1; i >= 0; i--, row--)
                {
                    System.Console.SetCursorPosition(0, row);
                    System.Console.Write(lines[i]);
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
            if (width <= 0) { result.Add(text); return result; }
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
