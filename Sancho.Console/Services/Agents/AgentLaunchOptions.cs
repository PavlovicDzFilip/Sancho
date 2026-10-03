using System.Diagnostics;

namespace Sancho.Console.Agents;

/// <summary>Shared launch context. Adapters translate instructions and permissions into native CLI options.</summary>
public sealed record AgentLaunchOptions
{
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public string? Instructions { get; init; }
    public string? Executable { get; init; }
    public IReadOnlyList<string> PrefixArguments { get; init; } = Array.Empty<string>();

    /// <summary>Resolve instructions once, in the target directory, without changing the process-wide directory.</summary>
    public string ReadInstructions()
    {
        if (Instructions is not null)
            return Instructions.Trim();
        var path = Path.Combine(WorkingDirectory, ".sancho.md");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }

    /// <summary>Build a shell-free launch, preserving every argument verbatim. Prefix arguments support a test host.</summary>
    public ProcessStartInfo CreateStartInfo(string defaultExecutable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Executable ?? defaultExecutable,
            WorkingDirectory = Path.GetFullPath(WorkingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in PrefixArguments.Concat(arguments))
            info.ArgumentList.Add(argument);
        return info;
    }
}
