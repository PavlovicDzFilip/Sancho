using System.Globalization;

namespace Sancho.Console.Orchestration;

/// <summary>Stable meeting-file names and local timestamps, supplied by the event consumer.</summary>
internal static class MeetingTranscript
{
    internal static string FilePath(string directory, bool notesMode, DateTimeOffset sessionStarted) =>
        Path.Combine(directory, $"sancho-{(notesMode ? "notes" : "meeting")}-{sessionStarted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md");

    internal static string SessionHeader(DateTimeOffset sessionStarted) =>
        $"## Meeting session — {Timestamp(sessionStarted)}";

    internal static string FormatLine(DateTimeOffset receivedAt, bool fromMic, string transcript) =>
        $"[{Timestamp(receivedAt)}] {(fromMic ? "Me" : "Others")}: {transcript.Trim().Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')}";

    private static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
