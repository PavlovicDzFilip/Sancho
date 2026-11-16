using System.Diagnostics;
using Sancho.Console.Cli;
using Xunit;

namespace Sancho.Tests;

public class CliSurfaceTests
{
    private static async Task<(int ExitCode, string Output)> Run(string configDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sancho.Tests.runtimeconfig.json"),
                     "--depsfile", Path.Combine(AppContext.BaseDirectory, "Sancho.Tests.deps.json"), typeof(CliArgs).Assembly.Location }.Concat(arguments))
            info.ArgumentList.Add(argument);
        info.Environment["SANCHO_CONFIG_DIR"] = configDirectory;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = Process.Start(info)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Theory]
    [InlineData(0, "--help", "--help")]
    [InlineData(0, "<value>", "config", "set", "--help")]
    [InlineData(0, "0.1.0", "-v")]
    [InlineData(2, "Required argument missing", "--agent")]
    [InlineData(2, "Unrecognized", "--unknown")]
    [InlineData(2, "Unknown command", "[suggest]")]
    [InlineData(2, "Required argument missing", "config", "set", "agent")]
    [InlineData(2, "Agent arguments require", "--notes", "--", "--help")]
    public async Task EarlyExitDoesNotCreateConfigOrStartServices(int expectedExit, string expectedText, params string[] arguments)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-cli-" + Guid.NewGuid());
        try
        {
            var result = await Run(directory, arguments);
            Assert.Equal(expectedExit, result.ExitCode);
            Assert.Contains(expectedText, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ConfigSetAndGetRoundTripThroughRealEntryPoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sancho-cli-config-" + Guid.NewGuid());
        try
        {
            var set = await Run(directory, "config", "set", "agent", "claude");
            Assert.Equal(0, set.ExitCode);
            var get = await Run(directory, "config", "get", "agent");
            Assert.Equal(0, get.ExitCode);
            Assert.Equal("claude", get.Output.Trim());
            Assert.True(File.Exists(Path.Combine(directory, "config.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
