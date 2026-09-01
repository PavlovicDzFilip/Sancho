namespace Sancho.Console.Cli;

/// <summary>Thrown for malformed command lines; the message is shown with a usage hint.</summary>
public sealed class UsageError(string message) : Exception(message);

/// <summary>Parsed command line for <c>sancho</c>.</summary>
public sealed record CliArgs(
    bool Continue,
    string? ApiKey,
    string? Transcription,
    string? Command,
    string[] CommandArgs,
    bool ShowHelp,
    bool ShowVersion)
{
    /// <summary>
    /// Parses the command line: <c>--flag value</c>, <c>--flag=value</c>,
    /// <c>-c</c>, and the <c>config</c> subcommand. Unknown flags/commands throw
    /// <see cref="UsageError"/>.
    /// </summary>
    public static CliArgs Parse(string[] args)
    {
        var continueSession = false;
        string? apiKey = null;
        string? transcription = null;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    return new CliArgs(false, null, null, null, [], true, false);
                case "-v" or "--version":
                    return new CliArgs(false, null, null, null, [], false, true);
                case "-c" or "--continue":
                    continueSession = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        var (name, inlineValue) = SplitFlag(arg);
                        var value = inlineValue;
                        if (value is null && i + 1 < args.Length &&
                            !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            value = args[++i];
                        }

                        switch (name)
                        {
                            case "--api-key":
                                apiKey = value ?? throw new UsageError("'--api-key' requires a value.");
                                break;
                            case "--transcription":
                                transcription = value ?? throw new UsageError("'--transcription' requires a value.");
                                break;
                            default:
                                throw new UsageError($"Unknown option '{name}'.");
                        }
                    }
                    else
                    {
                        positional.Add(arg);
                    }
                    break;
            }
        }

        if (positional.Count > 0)
        {
            if (positional[0] is not "config")
                throw new UsageError($"Unknown command '{positional[0]}'.");
            return new CliArgs(continueSession, apiKey, transcription,
                "config", positional.Skip(1).ToArray(), false, false);
        }

        return new CliArgs(continueSession, apiKey, transcription, null, [], false, false);
    }

    private static (string Name, string? Value) SplitFlag(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq >= 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
    }
}
