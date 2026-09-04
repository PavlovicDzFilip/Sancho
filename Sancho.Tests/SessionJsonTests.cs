using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

public class SessionJsonTests
{
    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sancho-session-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void EncodeProjectDirectory_KeepsAlphanumerics_DashesTheRest()
    {
        // ':' and '\' each become one dash, so the drive prefix yields two.
        Assert.Equal("C--Users-pavlo-dev-Sancho", SessionJson.EncodeProjectDirectory(@"C:\Users\pavlo\dev\Sancho"));
        Assert.Equal("D--MBank", SessionJson.EncodeProjectDirectory("D:\\MBank"));
    }

    [Fact]
    public void ClaudeCodeFormat_ReadsTypeEnvelope()
    {
        var path = TempFile(
            """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"hello there"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi back"}]}}
            {"type":"system","subtype":"init"}
            """);

        var messages = SessionJson.ReadMessages(path, SessionJson.Format.ClaudeCode);

        Assert.Equal([(true, "hello there"), (false, "hi back")], messages);
    }

    [Fact]
    public void CursorFormat_ReadsTopLevelRole()
    {
        var path = TempFile(
            """
            {"role":"user","message":{"content":[{"type":"text","text":"hello there"}]}}
            {"role":"assistant","message":{"content":[{"type":"text","text":"hi back"}]}}
            """);

        var messages = SessionJson.ReadMessages(path, SessionJson.Format.Cursor);

        Assert.Equal([(true, "hello there"), (false, "hi back")], messages);
    }

    [Fact]
    public void CodexFormat_ReadsPayloadEnvelope()
    {
        var path = TempFile(
            """
            {"timestamp":"2026-04-05T11:39:01.047Z","type":"session_meta","payload":{"id":"019d5d70-53c0-7563-b818-8cd330cec0b5"}}
            {"timestamp":"2026-04-05T11:39:02.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hello there"}]}}
            {"timestamp":"2026-04-05T11:39:03.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi back"}]}}
            {"timestamp":"2026-04-05T11:39:04.000Z","type":"event_msg","payload":{"type":"task_started"}}
            """);

        var messages = SessionJson.ReadMessages(path, SessionJson.Format.Codex);

        Assert.Equal([(true, "hello there"), (false, "hi back")], messages);
    }

    [Fact]
    public void AutoFormat_FallsBackAcrossFormats()
    {
        var path = TempFile(
            """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"claude line"}]}}
            {"role":"user","message":{"content":[{"type":"text","text":"cursor line"}]}}
            {"timestamp":"x","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"codex line"}]}}
            """);

        var messages = SessionJson.ReadMessages(path, SessionJson.Format.Auto);

        Assert.Equal(
            [(true, "claude line"), (true, "cursor line"), (true, "codex line")],
            messages);
    }

    [Fact]
    public void Preview_TakesFirstUserTextAndTruncates()
    {
        var path = TempFile(
            $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"{new string('a', 200)}\"}}]}}}}");

        var preview = SessionJson.GetSessionPreview(path, SessionJson.Format.ClaudeCode);

        Assert.Equal(120, preview.Length);
    }

    [Fact]
    public void Preview_NoMessages()
    {
        var path = TempFile("{\"type\":\"system\",\"subtype\":\"init\"}");

        Assert.Equal("(no messages)", SessionJson.GetSessionPreview(path, SessionJson.Format.ClaudeCode));
    }

    [Fact]
    public void MalformedLines_AreSkipped()
    {
        var path = TempFile(
            """
            not json at all
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"real one"}]}}
            """);

        var messages = SessionJson.ReadMessages(path, SessionJson.Format.ClaudeCode);

        Assert.Equal([(true, "real one")], messages);
    }
}
