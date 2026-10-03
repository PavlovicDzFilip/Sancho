using System.Diagnostics;
using System.Text.Json;
using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

public class AgentLaunchOptionsTests
{
    [Fact]
    public async Task ShellFreeLaunchPreservesArgumentsDirectoryAndInput()
    {
        var options = new AgentLaunchOptions
        {
            Executable = "dotnet",
            PrefixArguments = [Path.Combine(AppContext.BaseDirectory, "process-test-host", "Sancho.ProcessTestHost.dll")],
            WorkingDirectory = Path.GetTempPath()
        };
        using var process = Process.Start(options.CreateStartInfo("unused", ["echo", "two words", "a\"b", "$(literal)"]))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var first = await process.StandardOutput.ReadLineAsync(timeout.Token);
            using var json = JsonDocument.Parse(first!);
            Assert.Equal(new[] { "two words", "a\"b", "$(literal)" }, json.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.WorkingDirectory)), Path.TrimEndingDirectorySeparator(json.RootElement.GetProperty("directory").GetString()!));
            await process.StandardInput.WriteLineAsync("first turn");
            await process.StandardInput.WriteLineAsync("second turn");
            process.StandardInput.Close();
            Assert.Equal("first turn", await process.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.Equal("second turn", await process.StandardOutput.ReadLineAsync(timeout.Token));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void ExplicitInstructionsOverrideTargetFile()
    {
        Assert.Equal("chosen instructions", new AgentLaunchOptions { Instructions = " chosen instructions " }.ReadInstructions());
        Assert.Equal(string.Empty, new AgentLaunchOptions { Instructions = string.Empty }.ReadInstructions());
    }

    [Fact]
    public void InstructionsComeFromTargetDirectoryAndMissingFileIsEmpty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new AgentLaunchOptions { WorkingDirectory = directory };
            Assert.Equal(string.Empty, options.ReadInstructions());
            File.WriteAllText(Path.Combine(directory, ".sancho.md"), " target instructions\n");
            Assert.Equal("target instructions", options.ReadInstructions());
            Assert.Equal("override", (options with { Instructions = "override" }).ReadInstructions());
        }
        finally
        {
            File.Delete(Path.Combine(directory, ".sancho.md"));
            Directory.Delete(directory);
        }
    }
}
