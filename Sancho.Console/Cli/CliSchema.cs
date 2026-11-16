using System.CommandLine;

namespace Sancho.Console.Cli;

/// <summary>Explicit command definitions shared by parsing and generated help; no reflection or service startup.</summary>
internal static class CliSchema
{
    internal static RootCommand CreateCommand(string[]? rawArguments = null)
    {
        var root = new RootCommand("Sancho — voice assistant: microphone → on-device transcription → your agent CLI.");
        root.Directives.Clear();
        root.SetAction(_ => 0);
        root.Options.Single(option => option.Name == "--version").Aliases.Add("-v");
        root.Options.Single(option => option.Name == "--version").Recursive = true;
        root.Options.Add(new Option<bool>("--continue", "-c")
        {
            Description = "Pick a previous agent session to resume.", Recursive = true
        });
        root.Options.Add(new Option<string?>("--agent")
        {
            Description = "Agent: claude, cursor, hermes, codex or dummy (auto-detected when unset).",
            Recursive = true, Arity = ArgumentArity.ExactlyOne
        });
        root.Options.Add(new Option<string?>("--model")
        {
            Description = "Whisper size: tiny, base, small (default) or medium.",
            Recursive = true, Arity = ArgumentArity.ExactlyOne
        });
        var directory = new Option<string?>("--dir")
        {
            Description = "Working directory (default: current directory).",
            Recursive = true, Arity = ArgumentArity.ExactlyOne
        };
        root.Options.Add(directory);
        // Keep last-value-wins for repeated options, validating every occurrence.
        foreach (var option in root.Options.OfType<Option<string?>>())
        {
            option.AllowMultipleArgumentsPerToken = true;
            option.CustomParser = result => result.Tokens.Count == 0 ? null : result.Tokens[^1].Value;
            var occurrences = (rawArguments ?? []).Where(arg => arg == option.Name ||
                arg.StartsWith(option.Name + "=", StringComparison.Ordinal)).ToArray();
            option.Validators.Add(result =>
            {
                if (result.Tokens.Count != result.IdentifierTokenCount)
                    result.AddError($"'{option.Name}' requires a value for every occurrence.");
                for (var i = 0; i < result.Tokens.Count; i++)
                {
                    var value = result.Tokens[i].Value;
                    if (option.Name == "--dir" && string.IsNullOrWhiteSpace(value))
                        result.AddError("'--dir' requires a non-empty directory.");
                    // Inline --dir=--project is explicit; a separate --notes is not a value.
                    if (value.StartsWith("--", StringComparison.Ordinal) &&
                        (i >= occurrences.Length || !occurrences[i].StartsWith(option.Name + "=", StringComparison.Ordinal)))
                        result.AddError($"'{option.Name}' requires a value, not another option.");
                }
            });
        }
        root.Options.Add(new Option<bool>("--notes")
        {
            Description = "Append dictation to sancho-notes-YYYY-MM-DD.md in the working directory; no agent.", Recursive = true
        });
        root.Options.Add(new Option<bool>("--meeting")
        {
            Description = "Transcribe mic and system audio, labeled Me/Others (Windows/Linux).", Recursive = true
        });
        root.Options.Add(new Option<bool>("--log")
        {
            Description = "Mirror this run and agent diagnostics to ~/.sancho/sancho.log; not persisted.", Recursive = true
        });

        var config = new Command("config", "View or persist user configuration.");
        var get = new Command("get", "Show all configuration or a single key.")
        {
            new Argument<string?>("key") { Description = "agent or model", Arity = ArgumentArity.ZeroOrOne }
        };
        var set = new Command("set", "Persist one configuration key.")
        {
            new Argument<string>("key") { Description = "agent or model" },
            new Argument<string>("value") { Description = "Configuration value" }
        };
        get.SetAction(_ => 0);
        set.SetAction(_ => 0);
        config.Subcommands.Add(get);
        config.Subcommands.Add(set);
        root.Subcommands.Add(config);
        return root;
    }

    internal static ParserConfiguration ParserConfiguration() => new()
    {
        // Preserve literal @values; caller arguments after -- bypass parsing entirely.
        ResponseFileTokenReplacer = null,
        EnablePosixBundling = false
    };
}
