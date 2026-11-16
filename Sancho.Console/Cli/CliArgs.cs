using System.CommandLine;

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
    public string[] HelpCommandPath { get; init; } = [];

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

    /// <summary>Parse only Sancho arguments; the raw suffix after the first -- never reaches the library.</summary>
    public static CliArgs Parse(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        var sanchoArgs = separator < 0 ? args : args[..separator];
        var agentArguments = separator < 0 ? [] : args[(separator + 1)..];
        // Clearing library directives does not stop its tokenizer from silently skipping them.
        if (sanchoArgs.FirstOrDefault() is { } first && first.StartsWith('[') && first.EndsWith(']'))
            throw new UsageError($"Unknown command '{first}'.");
        var root = CliSchema.CreateCommand(sanchoArgs);
        var parsed = root.Parse(sanchoArgs, CliSchema.ParserConfiguration());
        var help = parsed.GetResult("--help") is not null;
        var version = parsed.GetResult("--version") is not null;
        if (help && version)
        {
            var tokens = parsed.Tokens.ToList();
            var helpToken = parsed.GetResult(root.Options.Single(option => option.Name == "--help"))!.IdentifierToken;
            var versionToken = parsed.GetResult(root.Options.Single(option => option.Name == "--version"))!.IdentifierToken;
            help = tokens.IndexOf(helpToken!) < tokens.IndexOf(versionToken!);
            version = !help;
        }
        var command = parsed.CommandResult.Command;
        var commandPath = command == root ? Array.Empty<string>()
            : command.Name == "config" ? ["config"] : new[] { "config", command.Name };

        if (help || version)
            return new CliArgs(false, null, null, null, [], help, version, false, false, false)
            {
                HelpCommandPath = commandPath
            };

        if (parsed.Errors.Count > 0)
            throw new UsageError(string.Join(Environment.NewLine, parsed.Errors.Select(error => error.Message)));

        var agent = parsed.GetValue<string?>("--agent");
        var notes = parsed.GetValue<bool>("--notes");
        if (agentArguments.Length > 0 &&
            (command != root || notes || string.Equals(agent, "dummy", StringComparison.OrdinalIgnoreCase)))
            throw new UsageError("Agent arguments require an agent backend, not config, notes mode or dummy.");

        string[] commandArgs = command.Name switch
        {
            "get" => parsed.GetValue<string?>("key") is { } key ? ["get", key] : ["get"],
            "set" => ["set", parsed.GetValue<string>("key")!, parsed.GetValue<string>("value")!],
            _ => []
        };

        return new CliArgs(parsed.GetValue<bool>("--continue"), agent,
            parsed.GetValue<string?>("--model"), command == root ? null : "config", commandArgs,
            help, version, parsed.GetValue<bool>("--log"), notes, parsed.GetValue<bool>("--meeting"))
        {
            DirectoryPath = parsed.GetValue<string?>("--dir"),
            AgentArguments = agentArguments,
            HelpCommandPath = commandPath
        };
    }
}
