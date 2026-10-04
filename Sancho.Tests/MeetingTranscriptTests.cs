using Sancho.Console.Orchestration;
using Xunit;

namespace Sancho.Tests;

public class MeetingTranscriptTests
{
    [Theory]
    [InlineData(true, "Me")]
    [InlineData(false, "Others")]
    public void LineIncludesDateOffsetAndSpeaker(bool fromMic, string label)
    {
        var received = new DateTimeOffset(2026, 10, 4, 14, 5, 6, TimeSpan.FromHours(2));
        Assert.Equal($"[2026-10-04T14:05:06+02:00] {label}: hello there", MeetingTranscript.FormatLine(received, fromMic, "  hello there  "));
    }

    [Fact]
    public void LinesRemainUnambiguousAcrossMidnightAndOffsetChanges()
    {
        Assert.StartsWith("[2026-10-24T23:59:59+02:00]", MeetingTranscript.FormatLine(new DateTimeOffset(2026, 10, 24, 23, 59, 59, TimeSpan.FromHours(2)), true, "before midnight"));
        Assert.StartsWith("[2026-10-25T00:00:01+02:00]", MeetingTranscript.FormatLine(new DateTimeOffset(2026, 10, 25, 0, 0, 1, TimeSpan.FromHours(2)), false, "after midnight"));
        Assert.StartsWith("[2026-10-25T02:05:00+01:00]", MeetingTranscript.FormatLine(new DateTimeOffset(2026, 10, 25, 2, 5, 0, TimeSpan.FromHours(1)), false, "after clocks change"));
    }

    [Theory]
    [InlineData(false, "sancho-meeting-2026-10-04.md")]
    [InlineData(true, "sancho-notes-2026-10-04.md")]
    public void MeetingFilenamePreservesNotesFlagCompatibility(bool notesMode, string expected)
    {
        var started = new DateTimeOffset(2026, 10, 4, 0, 1, 2, TimeSpan.FromHours(2));
        Assert.Equal(Path.Combine("target", expected), MeetingTranscript.FilePath("target", notesMode, started));
        Assert.Equal("## Meeting session — 2026-10-04T00:01:02+02:00", MeetingTranscript.SessionHeader(started));
    }

    [Fact]
    public void MultilineUtteranceKeepsOneTimestampedLine()
    {
        var started = new DateTimeOffset(2026, 10, 4, 0, 1, 2, TimeSpan.Zero);
        Assert.Equal("[2026-10-04T00:01:02+00:00] Me: first second third", MeetingTranscript.FormatLine(started, true, "first\r\nsecond\nthird"));
    }
}
