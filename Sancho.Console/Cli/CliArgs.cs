namespace Sancho.Console.Cli;

/// <summary>Thrown for malformed command lines; the message is shown with a usage hint.</summary>
public sealed class UsageError(string message) : Exception(message);

/// <summary>Parsed command line for <c>sancho</c>.</summary>
public sealed record CliArgs(
    bool Continue,
    string? Command,
    string[] CommandArgs,
    bool ShowHelp,
    bool ShowVersion,
    bool Log)
{
    /// <summary>
    /// Parses the command line: <c>-c</c>, <c>--log</c>, and the <c>config</c>
    /// subcommand. Unknown flags/commands throw <see cref="UsageError"/>.
    /// </summary>
    public static CliArgs Parse(string[] args)
    {
        var continueSession = false;
        var log = false;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    return new CliArgs(false, null, [], true, false, false);
                case "-v" or "--version":
                    return new CliArgs(false, null, [], false, true, false);
                case "-c" or "--continue":
                    continueSession = true;
                    break;
                case "--log":
                    log = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        var (name, _) = SplitFlag(arg);
                        throw new UsageError($"Unknown option '{name}'.");
                    }

                    positional.Add(arg);
                    break;
            }
        }

        if (positional.Count > 0)
        {
            if (positional[0] is not "config")
                throw new UsageError($"Unknown command '{positional[0]}'.");
            return new CliArgs(continueSession, "config", positional.Skip(1).ToArray(), false, false, log);
        }

        return new CliArgs(continueSession, null, [], false, false, log);
    }

    private static (string Name, string? Value) SplitFlag(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq >= 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
    }
}
