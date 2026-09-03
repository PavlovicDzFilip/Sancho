namespace Sancho.Console;

/// <summary>
/// Two-panel display: History scrolls naturally in the console,
/// Transcript is pinned at the bottom via ANSI scroll region.
/// </summary>
public sealed class Display : IDisposable
{
    // ── Transcript geometry ───────────────────────────────────────
    private const int ContentRows = 4; // queued/delta lines below the bar
    private const int StatusRows = 1;  // status line above the bar
    private const int BarRows = 1;     // plain separator
    private const int TranscriptRows = ContentRows + StatusRows + BarRows; // 6 pinned rows
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

    /// <summary>True when stdout is redirected — no usable console to pin the TUI to.</summary>
    private static bool Redirected => System.Console.IsOutputRedirected;

    public void Start()
    {
        if (Redirected)
            return; // no console: the TUI degrades to plain lines
        // The cursor jumps between rows on every panel draw and its blink is
        // pure noise here — hide it for the run. Purely cosmetic: if the
        // terminal can't do it, keep going.
        try
        {
            System.Console.CursorVisible = false;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }

        // Reserve the bottom rows via ANSI scroll region — the region ends
        // above the transcript panel so history never overlaps it.
        var total = System.Console.WindowHeight;
        if (total > TranscriptRows)
            System.Console.Write($"\e[1;{total - TranscriptRows}r");
        System.Console.Clear();
    }

    /// <summary>Re-apply the scroll region (e.g. after terminal resize).</summary>
    internal void RefreshScrollRegion()
    {
        if (Redirected)
            return;
        var total = System.Console.WindowHeight;
        if (total <= TranscriptRows) return;
        System.Console.Write($"\e[r"); // reset first
        System.Console.Write($"\e[1;{total - TranscriptRows}r");
    }

    public void Dispose()
    {
        System.Console.Write("\e[r"); // reset scroll region
        try
        {
            System.Console.CursorVisible = true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }
    }

    // ── ANSI helpers ──────────────────────────────────────────────

    internal static string FormatLine(string text, HistoryColor color)
    {
        return $"{color.Ansi}{text}{HistoryColor.Default.Ansi}";
    }

    /// <summary>
    /// Remembers the cursor position for <see cref="RestoreCursorPosition"/>.
    /// On Unix this uses the terminal-side ANSI save (<c>ESC 7</c>) instead of
    /// <see cref="System.Console.GetCursorPosition"/>: the API version issues
    /// a cursor-position query (<c>ESC[6n</c>) and reads the reply from stdin,
    /// which races with the keyTask's <see cref="System.Console.ReadKey"/>
    /// raw-mode reader and intermittently hangs the status draw.
    /// </summary>
    internal static (int Left, int Top) SaveCursorPosition()
    {
        if (Redirected)
            return default;
        if (OperatingSystem.IsWindows())
            return System.Console.GetCursorPosition();

        System.Console.Write("\e7"); // DECSC — the terminal remembers, we don't query
        return default;
    }

    /// <summary>Returns the cursor to a position saved by <see cref="SaveCursorPosition"/>.</summary>
    internal static void RestoreCursorPosition((int Left, int Top) pos)
    {
        if (Redirected)
            return;
        if (OperatingSystem.IsWindows())
            System.Console.SetCursorPosition(pos.Left, pos.Top);
        else
            System.Console.Write("\e8"); // DECRC
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
                if (!Redirected)
                {
                    // Write at the last row of the scroll region: the line
                    // appears at the bottom of the history area and the region
                    // scrolls up. Writing at the natural cursor position would
                    // overwrite the lines already on screen (e.g. the startup
                    // banner) until the cursor reaches the region bottom.
                    var bottom = System.Console.WindowHeight - TranscriptRows - 1;
                    if (bottom >= 0)
                        System.Console.SetCursorPosition(0, bottom);
                }

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
        private string[] _lastFrame = new string[TranscriptRows];
        private int _lastWidth;
        private int _lastHeight;

        internal TranscriptPanel(Display display) => _display = display;

        /// <param name="hint">
        /// Fallback line drawn on the bottom content row when there is no
        /// queued text or live delta — the "start talking" cue sits exactly
        /// where the next transcribed sentence will appear.
        /// </param>
        public void Set(
            IReadOnlyList<string> queued,
            string? currentDelta,
            string? hint = null,
            HistoryColor? hintColor = null)
        {
            lock (_display._renderLock)
            {
                if (Redirected)
                {
                    // No console to pin the panel to — fall back to plain lines.
                    foreach (var q in queued)
                        System.Console.WriteLine($"  ⏳ {q}");
                    if (currentDelta is { Length: > 0 })
                        System.Console.WriteLine($"  ▶ {currentDelta}");
                    return;
                }

                var total = System.Console.WindowHeight;
                var width = System.Console.WindowWidth;
                var startRow = Math.Max(0, total - TranscriptRows);

                // Only re-apply the scroll region when the terminal was resized.
                var sizeChanged = total != _lastHeight || width != _lastWidth;
                if (sizeChanged)
                {
                    _lastHeight = total;
                    _lastWidth = width;
                    _display.RefreshScrollRegion();
                }

                var (left, top) = SaveCursorPosition();

                // Build wrapped content lines
                var lines = new List<string>();
                foreach (var q in queued)
                    lines.AddRange(Wrap($"  ⏳ {q}", width));
                if (currentDelta is { Length: > 0 })
                    lines.AddRange(Wrap($"  ▶ {currentDelta}", width));
                else if (lines.Count == 0 && hint is { Length: > 0 })
                {
                    // The hint replaces itself in place: once a delta or
                    // sentence lands, the same bottom row holds real text.
                    lines.AddRange(Wrap(
                        FormatLine($"  {hint}", hintColor ?? HistoryColor.Dim), width));
                }

                // Panel layout: content rows at the bottom, the plain
                // separator bar above them, the status row at the top
                // (drawn by SetStatus, not part of the frame). Content is
                // ordered top-down: the latest line (live delta) sits on the
                // bottom row and earlier sentences stack above it.
                var frame = new string[TranscriptRows];
                frame[BarRows] = new string('─', width);
                var contentRows = Math.Min(lines.Count, ContentRows);
                for (var k = 0; k < contentRows; k++)
                    frame[TranscriptRows - 1 - k] = lines[lines.Count - 1 - k];

                // Clear the whole transcript area on resize (rows may have moved).
                if (sizeChanged)
                {
                    for (var i = 0; i < TranscriptRows; i++)
                    {
                        System.Console.SetCursorPosition(0, startRow + i);
                        System.Console.Write(new string(' ', width));
                    }
                }

                // Write only the rows that changed since the last frame —
                // avoids a full redraw (and the flicker that comes with it)
                // on every transcription delta.
                for (var i = 0; i < TranscriptRows; i++)
                {
                    var text = frame[i] ?? "";
                    if (sizeChanged || _lastFrame[i] != text)
                    {
                        System.Console.SetCursorPosition(0, startRow + i);
                        System.Console.Write(new string(' ', width));
                        System.Console.SetCursorPosition(0, startRow + i);
                        System.Console.Write(text);
                        _lastFrame[i] = text;
                    }
                }

                // Restore cursor
                RestoreCursorPosition((left, top));
            }
        }

        /// <summary>Updates the status line shown above the separator bar.</summary>
        public void SetStatus(string text, HistoryColor color)
        {
            lock (_display._renderLock)
            {
                _statusText = text;
                _statusColor = color;
                DrawStatusRow();
            }
        }

        /// <summary>Draws the status line above the bar, left-aligned.</summary>
        private void DrawStatusRow()
        {
            if (Redirected)
            {
                if (_statusText is not null)
                    System.Console.WriteLine(_statusText);
                return;
            }

            var total = System.Console.WindowHeight;
            var width = System.Console.WindowWidth;
            var row = Math.Max(0, total - TranscriptRows);
            var (left, top) = SaveCursorPosition();

            var text = _statusText ?? "";
            if (text.Length > width)
                text = text[..width];
            System.Console.SetCursorPosition(0, row);
            System.Console.Write(FormatLine(
                text + new string(' ', width - text.Length),
                _statusColor ?? HistoryColor.Default));

            RestoreCursorPosition((left, top));
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