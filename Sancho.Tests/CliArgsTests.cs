using Sancho.Console.Cli;
using Xunit;

namespace Sancho.Tests;

public class CliArgsTests
{
    [Fact]
    public void SeparatorStopsSanchoOptionParsing()
    {
        var parsed = CliArgs.Parse(["--agent", "claude", "--", "--help", "--model", "opus", "two words", "--"]);
        Assert.Equal("claude", parsed.Agent);
        Assert.False(parsed.ShowHelp);
        Assert.Null(parsed.Model);
        Assert.Null(parsed.Command);
        Assert.Equal(new[] { "--help", "--model", "opus", "two words", "--" }, parsed.AgentArguments);
    }

    [Theory]
    [InlineData("--dir", ".")]
    [InlineData("--dir=.")]
    public void DirectoryOptionIsAccepted(params string[] args)
    {
        var parsed = CliArgs.Parse(args);
        Assert.Null(parsed.Command);
        Assert.Equal(".", parsed.DirectoryPath);
        Assert.Equal(Directory.GetCurrentDirectory(), parsed.ResolveWorkingDirectory());
    }

    [Fact]
    public void EmptySeparatorAndDirectoryDefaults()
    {
        var parsed = CliArgs.Parse(["--"]);
        Assert.Empty(parsed.AgentArguments);
        Assert.Null(parsed.DirectoryPath);
        Assert.Equal(Directory.GetCurrentDirectory(), parsed.ResolveWorkingDirectory());
    }

    [Theory]
    [InlineData("--dir")]
    [InlineData("--dir", "--")]
    [InlineData("--dir=")]
    [InlineData("--dir", " ")]
    [InlineData("--notes", "--", "--model", "opus")]
    [InlineData("--agent", "dummy", "--", "--verbose")]
    [InlineData("config", "get", "--", "--verbose")]
    public void InvalidDirectoryOrUnusedAgentArgumentsAreRejected(params string[] args)
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse(args));
    }

    [Fact]
    public void MissingDirectoryIsRejectedWithoutCreatingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "sancho-missing-" + Guid.NewGuid());
        Assert.Throws<UsageError>(() => CliArgs.Parse(["--dir", path]).ResolveWorkingDirectory());
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void SelectedDirectoryDoesNotChangeProcessDirectory()
    {
        var current = Directory.GetCurrentDirectory();
        var path = Path.GetTempPath();
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), CliArgs.Parse(["--dir", path]).ResolveWorkingDirectory());
        Assert.Equal(current, Directory.GetCurrentDirectory());
    }

    [Fact]
    public void TrailingDirectorySeparatorDoesNotChangeSessionStoreKey()
    {
        var current = Directory.GetCurrentDirectory();
        var plain = CliArgs.Parse(["--dir", current]).ResolveWorkingDirectory();
        var trailing = CliArgs.Parse(["--dir", current + Path.DirectorySeparatorChar]).ResolveWorkingDirectory();
        Assert.Equal(Sancho.Console.Agents.SessionJson.EncodeProjectDirectory(plain),
            Sancho.Console.Agents.SessionJson.EncodeProjectDirectory(trailing));
    }

    [Fact]
    public void RootDirectoryIsPreserved()
    {
        var root = Path.GetPathRoot(Directory.GetCurrentDirectory())!;
        Assert.Equal(root, CliArgs.Parse(["--dir", root]).ResolveWorkingDirectory());
    }

    [Fact]
    public void NoArgs_AllDefaults()
    {
        var a = CliArgs.Parse([]);

        Assert.False(a.Continue);
        Assert.Null(a.Agent);
        Assert.Null(a.Model);
        Assert.Null(a.Command);
        Assert.False(a.ShowHelp);
        Assert.False(a.ShowVersion);
        Assert.False(a.Log);
        Assert.False(a.Notes);
        Assert.False(a.Meeting);
    }

    [Fact]
    public void BoolFlags_Parse()
    {
        var a = CliArgs.Parse(["-c", "--log", "--notes", "--meeting"]);

        Assert.True(a.Continue);
        Assert.True(a.Log);
        Assert.True(a.Notes);
        Assert.True(a.Meeting);
    }

    [Fact]
    public void LogFlag_DoesNotConsumeNextFlag()
    {
        var a = CliArgs.Parse(["--log", "--notes"]);

        Assert.True(a.Log);
        Assert.True(a.Notes);
    }

    [Fact]
    public void AgentFlag_WithSeparateValue()
    {
        var a = CliArgs.Parse(["--agent", "cursor"]);

        Assert.Equal("cursor", a.Agent);
    }

    [Fact]
    public void AgentFlag_WithInlineValue()
    {
        var a = CliArgs.Parse(["--agent=hermes"]);

        Assert.Equal("hermes", a.Agent);
    }

    [Fact]
    public void AgentFlag_WithoutValueThrows()
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse(["--agent"]));
    }

    [Fact]
    public void AgentFlag_DoesNotConsumeNextFlag_Throws()
    {
        // --agent followed by another flag: the parser must not swallow
        // "--notes" as the value — it requires one and errors.
        Assert.Throws<UsageError>(() => CliArgs.Parse(["--agent", "--notes"]));
    }

    [Fact]
    public void ModelFlag_WithSeparateValue()
    {
        var a = CliArgs.Parse(["--model", "tiny"]);

        Assert.Equal("tiny", a.Model);
    }

    [Fact]
    public void ModelFlag_WithInlineValue()
    {
        var a = CliArgs.Parse(["--model=medium"]);

        Assert.Equal("medium", a.Model);
    }

    [Fact]
    public void ModelFlag_WithoutValueThrows()
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse(["--model"]));
    }

    [Fact]
    public void ModelFlag_DoesNotConsumeNextFlag_Throws()
    {
        // --model followed by another flag: the parser must not swallow
        // "--notes" as the value — it requires one and errors.
        Assert.Throws<UsageError>(() => CliArgs.Parse(["--model", "--notes"]));
    }

    [Fact]
    public void ConfigCommand_CarriesArgs()
    {
        var a = CliArgs.Parse(["config", "set", "agent", "cursor"]);

        Assert.Equal("config", a.Command);
        Assert.Equal(["set", "agent", "cursor"], a.CommandArgs);
    }

    [Fact]
    public void Help_ShortCircuits()
    {
        Assert.True(CliArgs.Parse(["--help"]).ShowHelp);
        Assert.True(CliArgs.Parse(["-h"]).ShowHelp);
    }

    [Fact]
    public void Version_ShortCircuits()
    {
        Assert.True(CliArgs.Parse(["--version"]).ShowVersion);
        Assert.True(CliArgs.Parse(["-v"]).ShowVersion);
    }

    [Fact]
    public void UnknownFlag_Throws()
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse(["--api-key", "x"]));
    }

    [Fact]
    public void UnknownCommand_Throws()
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse(["frobnicate"]));
    }
}
