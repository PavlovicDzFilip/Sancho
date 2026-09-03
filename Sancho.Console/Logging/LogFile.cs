using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Logging;

/// <summary>
/// Timestamped, ANSI-free sink for the run log. Complete lines are flushed
/// as they arrive; one shared instance and one lock keep the console mirror
/// and the logger provider from interleaving half-lines.
/// </summary>
public sealed class LogFileWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly StringBuilder _pending = new();

    public LogFileWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    /// <summary>Appends text; complete lines are flushed with a timestamp prefix.</summary>
    public void Write(string text)
    {
        lock (_lock)
        {
            _pending.Append(text);
            FlushCompleteLines();
        }
    }

    /// <summary>Appends text and forces a line break.</summary>
    public void WriteLine(string text)
    {
        lock (_lock)
        {
            _pending.Append(text);
            while (IndexOfLineBreak() >= 0)
                WritePendingLine(IndexOfLineBreak());
            if (_pending.Length > 0)
                WritePendingLine(_pending.Length);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_pending.Length > 0)
                WritePendingLine(_pending.Length);
            _writer.Dispose();
        }
    }

    private void FlushCompleteLines()
    {
        while (true)
        {
            var nl = IndexOfLineBreak();
            if (nl < 0)
                return;
            WritePendingLine(nl);
        }
    }

    private void WritePendingLine(int length)
    {
        var line = _pending.ToString(0, length);
        _pending.Remove(0, length);
        _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Clean(line)}");
    }

    private int IndexOfLineBreak()
    {
        for (var i = 0; i < _pending.Length; i++)
        {
            if (_pending[i] == '\n')
                return i + 1; // include the newline in the flushed chunk
        }

        return -1;
    }

    // CSI sequences (ESC [ ... final byte) and the DEC save/restore pair
    // (ESC 7 / ESC 8) written by the display.
    private static readonly Regex Ansi = new(
        "\\[(?:[0-9;?]*)[ -/]*[@-~]|[78]",
        RegexOptions.Compiled);

    // The TUI clears rows with long space runs and draws its separator with
    // long ─ runs; collapse both so mirrored frames stay readable.
    private static readonly Regex SpaceRun = new(" {4,}", RegexOptions.Compiled);
    private static readonly Regex DashRun = new("─{4,}", RegexOptions.Compiled);

    private static string Clean(string text)
    {
        var stripped = Ansi.Replace(text, "");
        stripped = SpaceRun.Replace(stripped, " ");
        stripped = DashRun.Replace(stripped, "──");
        return stripped.TrimEnd('\r');
    }
}

/// <summary>
/// Mirrors every console write to both the original console output and a
/// <see cref="LogFileWriter"/>. Installed via <c>Console.SetOut</c> so the
/// display's direct Console.Write calls are captured too, not just ILogger
/// output.
/// </summary>
public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly LogFileWriter _log;

    public TeeTextWriter(TextWriter console, LogFileWriter log)
    {
        _console = console;
        _log = log;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Flush() => _console.Flush();

    public override void Write(char value)
    {
        _console.Write(value);
        _log.Write(value.ToString());
    }

    public override void Write(string? value)
    {
        _console.Write(value);
        if (value is not null)
            _log.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _console.Write(buffer, index, count);
        _log.Write(new string(buffer, index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        _console.Write(buffer);
        _log.Write(buffer.ToString());
    }

    public override void WriteLine()
    {
        _console.WriteLine();
        _log.WriteLine("");
    }

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        _log.WriteLine(value ?? "");
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        _console.WriteLine(buffer);
        _log.WriteLine(buffer.ToString());
    }
}

/// <summary>
/// Captures the diagnostics that never reach the console (Debug/Trace) into
/// the run log. Information and above are already mirrored from the console
/// by the <see cref="TeeTextWriter"/>, so they are skipped here to avoid
/// duplicate lines.
/// </summary>
public sealed class FileLogProvider(LogFileWriter writer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(writer);

    public void Dispose()
    {
    }

    private sealed class FileLogger(LogFileWriter writer) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel < LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            writer.WriteLine($"[{logLevel.ToString().ToLowerInvariant()}] {formatter(state, exception)}");
            if (exception is not null)
                writer.WriteLine(exception.ToString());
        }
    }
}
