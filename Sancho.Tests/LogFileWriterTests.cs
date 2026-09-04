using System.Text.RegularExpressions;
using Sancho.Console.Logging;
using Xunit;

namespace Sancho.Tests;

public class LogFileWriterTests
{
    private static readonly Regex Timestamp =
        new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", RegexOptions.Compiled);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sancho-log-{Guid.NewGuid():N}.log");

    private static string ReadAll(string path) => File.ReadAllText(path);

    [Fact]
    public void WriteLine_PrefixesTimestamp()
    {
        var path = TempPath();
        using (var w = new LogFileWriter(path))
            w.WriteLine("hello");

        var line = ReadAll(path).TrimEnd();
        Assert.Matches(Timestamp, line);
        Assert.EndsWith(" hello", line);
    }

    [Fact]
    public void Write_BuffersUntilNewline()
    {
        var path = TempPath();
        using (var w = new LogFileWriter(path))
        {
            w.Write("part one ");
            // Nothing flushed yet — the file must still be empty. Length is
            // used because the writer holds the file open exclusively.
            Assert.Equal(0, new FileInfo(path).Length);
            w.Write("part two\n");
        }

        var content = ReadAll(path);
        Assert.Single(Regex.Matches(content, "\n").Cast<Match>());
        Assert.EndsWith("part one part two", content.TrimEnd());
    }

    [Fact]
    public void WriteLine_WithEmbeddedNewlines_SplitsIntoTimestampedLines()
    {
        var path = TempPath();
        using (var w = new LogFileWriter(path))
            w.WriteLine("first\nsecond");

        var lines = ReadAll(path).TrimEnd().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Matches(Timestamp, l));
    }

    [Fact]
    public void AnsiSequences_AreStripped()
    {
        var path = TempPath();
        using (var w = new LogFileWriter(path))
            w.WriteLine("before \e[38;5;214mcolored\e[0m \e7after\e8");

        var content = ReadAll(path).TrimEnd();
        Assert.Matches(
            new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} before colored after$"),
            content);
    }

    [Fact]
    public void LongSpaceAndDashRuns_AreCollapsed()
    {
        var path = TempPath();
        using (var w = new LogFileWriter(path))
            w.WriteLine($"x{new string(' ', 40)}y{new string('─', 80)}z");

        var content = ReadAll(path);
        Assert.EndsWith("x y──z", content.TrimEnd());
    }

    [Fact]
    public void Dispose_FlushesTrailingPartialLine()
    {
        var path = TempPath();
        var w = new LogFileWriter(path);
        w.Write("trailing tail");
        w.Dispose();

        Assert.EndsWith("trailing tail", ReadAll(path).TrimEnd());
    }

    [Fact]
    public void NewWriter_OverwritesPreviousRun()
    {
        var path = TempPath();
        using (var w = new LogFileWriter(path))
            w.WriteLine("first run");
        using (var w = new LogFileWriter(path))
            w.WriteLine("second run");

        var content = ReadAll(path);
        Assert.DoesNotContain("first run", content);
        Assert.EndsWith("second run", content.TrimEnd());
    }
}
