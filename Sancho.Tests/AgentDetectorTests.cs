using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

/// <summary>
/// First-run agent discovery: an executable file in a PATH directory is found;
/// a missing one is not; non-executable files on Unix don't count.
/// </summary>
public class AgentDetectorTests
{
    [Fact]
    public void ExecutableInPath_IsFound()
    {
        using var dir = new TempDir();
        dir.WriteExecutable("claude");

        Assert.True(AgentDetector.IsOnPath("claude", dir.Path));
    }

    [Fact]
    public void MissingExecutable_IsNotFound()
    {
        using var dir = new TempDir();
        dir.WriteExecutable("cursor-agent");

        Assert.False(AgentDetector.IsOnPath("claude", dir.Path));
    }

    [Fact]
    public void Windows_MatchesPathextShims()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("PATHEXT shims only exist on Windows.");

        using var dir = new TempDir();
        dir.WriteFile("claude.cmd", "@echo off");

        Assert.True(AgentDetector.IsOnPath("claude", dir.Path));
    }

    [Fact]
    public void Unix_NonExecutableFile_IsNotFound()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Executable-bit semantics only exist on Unix.");

        using var dir = new TempDir();
        dir.WriteFile("claude", "#!/bin/sh\n");
        // No execute bit: on PATH but not runnable.

        Assert.False(AgentDetector.IsOnPath("claude", dir.Path));
    }

    /// <summary>Wipes itself on dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sancho-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void WriteExecutable(string name)
        {
            var full = WriteFile(name, "#!/bin/sh\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string WriteFile(string name, string content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }
}
