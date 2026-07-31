using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Sancho.Console;

/// <summary>
/// A console formatter that writes just the message with no category,
/// timestamp, or log-level prefix.  ANSI escape codes embedded in the
/// message are passed through unchanged.
/// </summary>
public sealed class RawConsoleFormatter : ConsoleFormatter, IDisposable
{
    private readonly IDisposable? _optionsReloadToken;

    public RawConsoleFormatter(IOptionsMonitor<SimpleConsoleFormatterOptions> options)
        : base("raw")
    {
        _optionsReloadToken = options.OnChange(_ => { });
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);

        // \0 prefix → no trailing newline (for streaming deltas)
        if (message.Length > 0 && message[0] == '\0')
            textWriter.Write(message.AsSpan(1));
        else
            textWriter.WriteLine(message);
    }

    public void Dispose() => _optionsReloadToken?.Dispose();
}
