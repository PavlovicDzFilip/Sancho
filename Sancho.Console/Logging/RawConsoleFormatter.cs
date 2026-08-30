using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Sancho.Console.Logging;

/// <summary>
/// Console formatter (name "raw") that emits just the message — no level,
/// category, or timestamp prefix — so ANSI escape codes in messages render
/// as-is. Exceptions are appended on their own line.
/// </summary>
public sealed class RawConsoleFormatter : ConsoleFormatter
{
    public RawConsoleFormatter() : base("raw") { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is not null)
            textWriter.WriteLine(message);
        if (logEntry.Exception is not null)
            textWriter.WriteLine(logEntry.Exception);
    }
}
