using System.Diagnostics;

namespace Sancho.Console.Agents;

/// <summary>Resolves Codex's native binary or official npm entrypoint without a command shell.</summary>
internal static class CodexCli
{
    internal sealed record Launch(string Executable, IReadOnlyList<string> PrefixArguments)
    {
        public ProcessStartInfo CreateStartInfo(AgentLaunchOptions options, IEnumerable<string> arguments) =>
            (options with { Executable = Executable, PrefixArguments = PrefixArguments.Concat(options.PrefixArguments).ToArray() })
                .CreateStartInfo(Executable, arguments);
    }

    internal static ProcessStartInfo CreateStartInfo(AgentLaunchOptions options, IEnumerable<string> arguments)
    {
        // Explicit executables/prefixes are used by embedders and the process test host.
        if (options.Executable is not null || options.PrefixArguments.Count > 0)
            return options.CreateStartInfo("codex", arguments);
        return ResolveLaunch(SearchDirectories(), OperatingSystem.IsWindows()).CreateStartInfo(options, arguments);
    }

    private static IEnumerable<string> SearchDirectories() =>
        new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) : null
        }.Where(path => path is not null)
            .SelectMany(path => path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Select(path => path.Trim().Trim('"')).Where(path => path.Length > 0)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static Launch ResolveLaunch(IEnumerable<string> directories, bool windows)
    {
        var paths = directories.ToArray();
        foreach (var directory in paths)
        {
            if (!windows)
            {
                var executable = Path.Combine(directory, "codex");
                if (AgentDetector.IsOnPath("codex", directory)) return new Launch(executable, Array.Empty<string>());
                continue;
            }

            foreach (var extension in new[] { ".exe", ".com" })
            {
                var executable = Path.Combine(directory, "codex" + extension);
                if (File.Exists(executable)) return new Launch(executable, Array.Empty<string>());
            }

            // npm's Windows .cmd/.ps1 wrappers cannot be launched with redirected
            // streams directly. Invoke the package entrypoint using Node instead;
            // the official entrypoint locates its platform binary and resources.
            if (!new[] { ".cmd", ".ps1", ".bat" }.Any(extension => File.Exists(Path.Combine(directory, "codex" + extension))))
                continue;
            var entrypoint = Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (!File.Exists(entrypoint)) continue;
            var node = new[] { directory }.Concat(paths)
                .Select(path => Path.Combine(path, "node.exe")).FirstOrDefault(File.Exists);
            if (node is not null) return new Launch(node, new[] { entrypoint });
        }
        throw new InvalidOperationException("Could not find a runnable `codex`. Install it with `npm install -g @openai/codex` (Node.js required), then run `codex login`.");
    }

    internal static bool IsInstalled()
    {
        try { _ = ResolveLaunch(SearchDirectories(), OperatingSystem.IsWindows()); return true; }
        catch (InvalidOperationException) { return false; }
    }
}
