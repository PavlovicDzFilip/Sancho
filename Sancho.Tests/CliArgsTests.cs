using Sancho.Console.Cli;
using Xunit;

namespace Sancho.Tests;

public class CliArgsTests
{
    [Fact]
    public void HelpRequestCanExplainAnOtherwiseInvalidCommand()
    {
        Assert.True(CliArgs.Parse(["--unknown", "--help"]).ShowHelp);
    }

    [Theory]
    [InlineData("--version", "--help", false)]
    [InlineData("-h", "-v", true)]
    [InlineData("/?", "--version", true)]
    [InlineData("/h", "--version", true)]
    [InlineData("--help=", "--version=", true)]
    [InlineData("--version=", "--help=", false)]
    public void FirstHelpOrVersionFlagWins(string first, string second, bool help)
    {
        var parsed = CliArgs.Parse([first, second]);
        Assert.Equal(help, parsed.ShowHelp);
        Assert.Equal(!help, parsed.ShowVersion);
    }

    [Theory]
    [InlineData("[suggest]")]
    [InlineData("[diagram]")]
    [InlineData("[anything]")]
    public void LeadingDirectivesAreRejected(string directive)
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse([directive]));
    }

    [Fact]
    public void RepeatedValueOptionsUseLastValue()
    {
        var parsed = CliArgs.Parse(["--agent", "claude", "--agent=codex", "--model=tiny", "--model", "small", "--dir", ".", "--dir=.."]);
        Assert.Equal("codex", parsed.Agent);
        Assert.Equal("small", parsed.Model);
        Assert.Equal("..", parsed.DirectoryPath);
    }

    [Theory]
    [InlineData("--agent", "claude", "--agent")]
    [InlineData("--dir", ".", "--dir=")]
    [InlineData("--dir", ".", "--dir", "--notes")]
    public void RepeatedValueOptionsStillValidateEveryOccurrence(params string[] args)
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse(args));
    }

    [Fact]
    public void ExplicitInlineDirectoryMayBeginWithDashes()
    {
        Assert.Equal("--project", CliArgs.Parse(["--dir=--project"]).DirectoryPath);
    }

    [Fact]
    public void RootHelpIsGeneratedFromRegisteredOptions()
    {
        var help = HelpText.Render();
        foreach (var option in new[] { "--agent", "--model", "--dir", "--continue", "--notes", "--meeting", "--log", "--help", "--version" })
            Assert.Contains(option, help);
        Assert.Contains("config", help);
        Assert.Contains("Everything after --", help);
    }

    [Theory]
    [InlineData("config")]
    [InlineData("config", "get")]
    [InlineData("config", "set")]
    public void SubcommandHelpHasItsOwnUsage(params string[] path)
    {
        var parsed = CliArgs.Parse([.. path, "--help"]);
        Assert.True(parsed.ShowHelp);
        Assert.Equal(path, parsed.HelpCommandPath);
        var help = HelpText.Render(path);
        Assert.Contains("config", help);
        Assert.DoesNotContain("Agent arguments:", help);
        if (path.Length == 2) Assert.Contains(path[1] == "set" ? "<value>" : "[<key>]", help);
    }

    [Theory]
    [InlineData("config", "get")]
    [InlineData("config", "get", "agent")]
    [InlineData("config", "set", "model", "@literal")]
    public void ConfigArgumentsArePreservedWithoutResponseFileExpansion(params string[] args)
    {
        Assert.Equal(args[1..], CliArgs.Parse(args).CommandArgs);
    }

    [Theory]
    [InlineData("config")]
    [InlineData("config", "set", "agent")]
    [InlineData("config", "set", "agent", "claude", "extra")]
    [InlineData("config", "get", "agent", "extra")]
    [InlineData("config", "unknown")]
    public void ConfigShapeIsValidatedBeforeDispatch(params string[] args)
    {
        Assert.Throws<UsageError>(() => CliArgs.Parse(args));
    }

    [Fact]
    public void PassthroughPreservesEmptyResponseFileAndDirectiveTokens()
    {
        string[] forwarded = ["", "@missing.rsp", "[suggest]", "a\"b", "--help", "--", "tail"];
        var parsed = CliArgs.Parse(["--agent", "claude", "--", .. forwarded]);
        Assert.Equal(forwarded, parsed.AgentArguments);
        Assert.False(parsed.ShowHelp);
    }

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
