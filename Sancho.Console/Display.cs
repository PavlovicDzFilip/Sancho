using System.Text;
using Spectre.Console;

namespace Sancho.Console;

/// <summary>
/// Two-panel live display: History (top) scrolls with conversation,
/// Input (bottom) shows live transcription and grows with content.
///
/// Registered as a singleton. Call <see cref="Start"/> once, then use
/// <see cref="UserInput"/> and <see cref="History"/> from any thread.
/// </summary>
public sealed class Display : IDisposable
{
    private readonly Layout _layout;
    private readonly Layout _historyLayout;
    private readonly Layout _inputLayout;

    private LiveDisplayContext? _ctx;
    private LiveDisplay? _live;
    private readonly ManualResetEventSlim _stopped = new();

    public Display()
    {
        _historyLayout = new Layout("History");
        _inputLayout = new Layout("Input");

        _historyLayout.Update(Panel("History", ""));
        _inputLayout.Update(Panel("Input", ""));

        _layout = new Layout("Root")
            .SplitRows(_historyLayout, _inputLayout);

        _historyLayout.Ratio(2);
        _inputLayout.MinimumSize(3);

        History = new HistoryPanel(this);
        UserInput = new UserInputPanel(this);
    }

    /// <summary>The top panel — conversation log.</summary>
    public HistoryPanel History { get; }

    /// <summary>The bottom panel — live user input.</summary>
    public UserInputPanel UserInput { get; }

    /// <summary>Start the live display. Call once before any writes.</summary>
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
        _stopped.Dispose();
    }

    // ----------------------------------------------------------------
    //  Internal — called by the nested panel classes
    // ----------------------------------------------------------------

    internal void RefreshHistory(string text)
    {
        _historyLayout.Update(Panel("History", text));
        _ctx?.Refresh();
    }

    internal void RefreshInput(string text)
    {
        var lineCount = text.Count(c => c == '\n') + 1;
        var panelHeight = Math.Max(3, lineCount + 2);

        _inputLayout.MinimumSize(panelHeight);
        _inputLayout.Update(Panel("Input", text));
        _ctx?.Refresh();
    }

    private static Panel Panel(string header, string text)
    {
        return new Panel(string.IsNullOrEmpty(text) ? "" : Markup.Escape(text))
            .Header(header)
            .Expand();
    }

    // ================================================================
    //  Nested panel classes
    // ================================================================

    /// <summary>
    /// The top panel that holds the full conversation log.
    /// </summary>
    public sealed class HistoryPanel
    {
        private readonly Display _display;
        private readonly StringBuilder _lines = new();

        internal HistoryPanel(Display display) => _display = display;

        /// <summary>Append a line to the history.</summary>
        public void Append(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            _lines.AppendLine(text);
            _display.RefreshHistory(_lines.ToString());
        }
    }

    /// <summary>
    /// The bottom panel that shows live transcription input.
    /// </summary>
    public sealed class UserInputPanel
    {
        private readonly Display _display;
        private readonly StringBuilder _buffer = new();

        internal UserInputPanel(Display display) => _display = display;

        /// <summary>Append partial transcription text.</summary>
        public void Append(string text)
        {
            _buffer.Append(text);
            _display.RefreshInput(_buffer.ToString());
        }

        /// <summary>
        /// Finalise the current input: move it to history, clear the input,
        /// and return the submitted text for the processing pipeline.
        /// </summary>
        public string Submit()
        {
            var text = _buffer.ToString().Trim();
            _buffer.Clear();

            if (text.Length > 0)
                _display.History.Append(text);

            _display.RefreshInput("");
            return text;
        }

        /// <summary>Clear in-progress input without submitting.</summary>
        public void Clear()
        {
            _buffer.Clear();
            _display.RefreshInput("");
        }
    }
}
