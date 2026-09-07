namespace Sancho.Console.Agents;

/// <summary>
/// First-run agent discovery: probes the PATH for each supported agent CLI, so
/// a fresh install with no <c>agent</c> config can pick the one the user
/// actually has. This is the cheap existence check only — the selected backend
/// still runs its own <c>VerifyAvailable</c> (installed + logged in) before
/// anything starts.
/// </summary>
public static class AgentDetector
{
    /// <summary>Supported agents: config name, PATH executable, install hint.</summary>
    public static readonly (string Name, string Executable, string InstallHint)[] Supported =
    [
        ("claude", "claude", "Install the Claude CLI: https://docs.anthropic.com/en/docs/claude-code/setup"),
        ("cursor", "cursor-agent", "Install Cursor from cursor.com"),
        ("hermes", "hermes", "Install hermes from hermes.dev"),
        ("codex", "codex", "Install the Codex CLI from OpenAI"),
    ];

    /// <summary>
    /// True when <paramref name="executable"/> resolves on the PATH — the same
    /// directories the agent services' Process.Start searches. Windows also
    /// matches PATHEXT shims (<c>claude.cmd</c>, …), which is where npm-style
    /// CLIs put themselves; Unix requires an executable file.
    /// </summary>
    /// <param name="pathEnv">Search path to use instead of the real PATH
    /// (tests only).</param>
    public static bool IsOnPath(string executable, string? pathEnv = null)
    {
        var path = pathEnv ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        var extensions = OperatingSystem.IsWindows() ? WindowsExtensions() : [""];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var full = Path.Combine(dir, executable + ext);
                if (!File.Exists(full))
                    continue;

                if (!OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(full);
                    if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute |
                                 UnixFileMode.OtherExecute)) == 0)
                        continue; // on PATH but not runnable — Process.Start would fail too
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// PATHEXT from the environment, plus the bare name (extensionless
    /// binaries exist too). Fallback covers PATHEXT being unset or mangled.
    /// </summary>
    private static string[] WindowsExtensions()
    {
        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var exts = string.IsNullOrEmpty(pathext)
            ? [".EXE", ".CMD", ".BAT", ".COM"]
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. exts, ""];
    }
}
