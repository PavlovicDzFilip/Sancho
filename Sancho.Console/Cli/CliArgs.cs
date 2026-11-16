namespace Sancho.Console.Cli;

/// <summary>Thrown for malformed command lines; the message is shown with a usage hint.</summary>
public sealed class UsageError(string message) : Exception(message);

/// <summary>Parsed command line for <c>sancho</c>.</summary>
public sealed record CliArgs(
    bool Continue,
    string? Agent,
    string? Model,
    string? Command,
    string[] CommandArgs,
    bool ShowHelp,
    bool ShowVersion,
    bool Log,
    bool Notes,
    bool Meeting)
{
    public string? DirectoryPath { get; init; }
    public string[] AgentArguments { get; init; } = [];

    public string ResolveWorkingDirectory()
    {
        try
        {
            var directory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(DirectoryPath ?? Directory.GetCurrentDirectory()));
            if (!Directory.Exists(directory))
                throw new UsageError($"Directory '{directory}' does not exist.");
            return directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new UsageError($"Invalid --dir: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the command line: <c>-c</c>, <c>--log</c>, <c>--notes</c>,
    /// <c>--meeting</c>, <c>--agent</c>, <c>--model</c>, <c>--dir</c>, and the <c>config</c>
    /// subcommand. Arguments after <c>--</c> belong to the agent.
    /// Unknown Sancho flags/commands throw <see cref="UsageError"/>.
    /// </summary>
    public static CliArgs Parse(string[] args)
    {
        var continueSession = false;
        string? agent = null;
        string? model = null;
        string? directory = null;
        string[] agentArguments = [];
        var log = false;
        var notes = false;
        var meeting = false;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                agentArguments = args[(i + 1)..];
                break;
            }
            switch (arg)
            {
                case "-h" or "--help":
                    return new CliArgs(false, null, null, null, [], true, false, false, false, false);
                case "-v" or "--version":
                    return new CliArgs(false, null, null, null, [], false, true, false, false, false);
                case "-c" or "--continue":
                    continueSession = true;
                    break;
                case "--log":
                    log = true;
                    break;
                case "--notes":
                    notes = true;
                    break;
                case "--meeting":
                    meeting = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        var (name, inlineValue) = SplitFlag(arg);
                        if (name == "--agent")
                        {
                            agent = ReadFlagValue(name, inlineValue, args, ref i);
                        }
                        else if (name == "--model")
                        {
                            model = ReadFlagValue(name, inlineValue, args, ref i);
                        }
                        else if (name == "--dir")
                        {
                            directory = ReadFlagValue(name, inlineValue, args, ref i);
                            if (string.IsNullOrWhiteSpace(directory))
                                throw new UsageError("'--dir' requires a non-empty directory.");
                        }
                        else
                        {
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
            if (agentArguments.Length > 0)
                throw new UsageError("Agent arguments cannot be used with a subcommand.");
            if (positional[0] is not "config")
                throw new UsageError($"Unknown command '{positional[0]}'.");
            return new CliArgs(continueSession, agent, model, "config", positional.Skip(1).ToArray(), false, false, log, notes, meeting);
        }

        if (agentArguments.Length > 0 && (notes || agent == "dummy"))
            throw new UsageError("Agent arguments require an agent backend, not notes mode or dummy.");

        return new CliArgs(continueSession, agent, model, null, [], false, false, log, notes, meeting)
        {
            DirectoryPath = directory,
            AgentArguments = agentArguments
        };
    }

    /// <summary>
    /// Reads a value-flag's value: inline (<c>--flag=x</c>) or as the next
    /// argument; a following flag is never consumed as the value.
    /// </summary>
    private static string ReadFlagValue(string name, string? inlineValue, string[] args, ref int i)
    {
        var value = inlineValue;
        if (value is null && i + 1 < args.Length &&
            !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[++i];
        }

        return value ?? throw new UsageError($"'{name}' requires a value.");
    }

    private static (string Name, string? Value) SplitFlag(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq >= 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
    }
}
