using Microsoft.Extensions.Logging;

namespace Sancho.Console;

/// <summary>
/// Extension methods on <see cref="ILogger"/> for inline (no-newline) output.
/// Works with <see cref="RawConsoleFormatter"/> which treats a leading \0
/// as "write without trailing newline".
/// </summary>
public static class LoggerExtensions
{
    /// <summary>Log a message without a trailing newline.</summary>
    public static void LogInline(this ILogger logger, LogLevel level, string message, params object[] args)
    {
        var text = args.Length > 0 ? string.Format(message, args) : message;
        logger.Log(level, 0, "\0" + text, null, static (state, _) => state);
    }

    public static void InfoInline(this ILogger logger, string message, params object[] args) =>
        logger.LogInline(LogLevel.Information, message, args);

    public static void WarnInline(this ILogger logger, string message, params object[] args) =>
        logger.LogInline(LogLevel.Warning, message, args);
}
