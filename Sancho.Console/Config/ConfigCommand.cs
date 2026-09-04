using Sancho.Console.Cli;
using Spectre.Console;

namespace Sancho.Console.Config;

/// <summary>Handles <c>sancho config get|set</c>.</summary>
public static class ConfigCommand
{
    public static int Handle(string[] args)
    {
        if (args.Length == 0)
            throw new UsageError("Usage: sancho config <get|set> ...");

        return args[0] switch
        {
            "get" => Get(args.AsSpan(1)),
            "set" => Set(args.AsSpan(1)),
            _ => throw new UsageError($"Unknown config command '{args[0]}'. Use 'sancho config get' or 'sancho config set'."),
        };
    }

    private static int Get(ReadOnlySpan<string> args)
    {
        if (args.Length > 1)
            throw new UsageError("Usage: sancho config get [key]");

        var config = ConfigStore.Load();

        if (args.Length == 1)
        {
            var value = ConfigStore.GetValue(config, args[0].ToString());
            AnsiConsole.MarkupLine(value ?? "");
            return 0;
        }

        AnsiConsole.MarkupLine($"[grey]Config file:[/] {SanchoPaths.ConfigFile}");
        foreach (var key in ConfigStore.KnownKeys)
        {
            var value = ConfigStore.GetValue(config, key);
            var display = value is null ? "[grey](not set)[/]" : Markup.Escape(value);
            AnsiConsole.MarkupLine($"  [bold]{key}[/]: {display}");
        }
        return 0;
    }

    private static int Set(ReadOnlySpan<string> args)
    {
        if (args.Length != 2)
            throw new UsageError("Usage: sancho config set <key> <value>");

        var key = args[0].ToString();
        var config = ConfigStore.Load();
        ConfigStore.Save(ConfigStore.WithKey(config, key, args[1].ToString()));
        AnsiConsole.MarkupLine($"[green]Saved[/] [bold]{Markup.Escape(key)}[/] to {SanchoPaths.ConfigFile}");
        return 0;
    }
}
