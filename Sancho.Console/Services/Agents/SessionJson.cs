using System.Text;
using System.Text.Json;

namespace Sancho.Console.Agents;

/// <summary>
/// Shared JSON helpers for agent session stores. Claude Code and Cursor keep
/// slightly different line formats: Claude Code wraps each line in a
/// <c>"type"</c> envelope with the role inside <c>message</c>; Cursor puts
/// the role at the top level next to <c>message</c>.
/// </summary>
internal static class SessionJson
{
    public enum Format
    {
        ClaudeCode,
        Cursor,
        Codex,
        Auto // try ClaudeCode first, then Cursor, then Codex — for unknown export formats
    }

    /// <summary>Encodes a directory path the way agent session stores name project folders.</summary>
    public static string EncodeProjectDirectory(string path)
    {
        var sb = new StringBuilder();
        foreach (var c in path)
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    /// <summary>Extracts the concatenated text blocks from a message object.</summary>
    public static string ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            // claude/cursor blocks are {"type":"text","text":...}; codex uses
            // {"type":"input_text"/"output_text","text":...}.
            if (block.TryGetProperty("type", out var bt)
                && bt.GetString() is "text" or "input_text" or "output_text"
                && block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(t.GetString());
            }
        }

        return sb.ToString();
    }

    /// <summary>Reads all user/assistant text messages from a session file, in chronological order.</summary>
    public static IReadOnlyList<(bool IsUser, string Text)> ReadMessages(string file, Format format)
    {
        var messages = new List<(bool IsUser, string Text)>();
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var entry = TryRead(doc.RootElement, format);
                if (entry is { } e)
                    messages.Add(e);
            }
            catch (JsonException)
            {
                // Malformed line — skip it.
            }
        }

        return messages;
    }

    /// <summary>Returns the first user/assistant text of a session file, for the picker.</summary>
    public static string GetSessionPreview(string file, Format format)
    {
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var entry = TryRead(doc.RootElement, format);
                if (entry is { Text: { Length: > 0 } text })
                    return text.Substring(0, Math.Min(120, text.Length));
            }
            catch (JsonException)
            {
                // Malformed line — skip it.
            }
        }

        return "(no messages)";
    }

    private static (bool IsUser, string Text)? TryRead(JsonElement root, Format format)
    {
        if (format == Format.Auto)
        {
            return TryRead(root, Format.ClaudeCode)
                ?? TryRead(root, Format.Cursor)
                ?? TryRead(root, Format.Codex);
        }

        if (format == Format.Codex)
        {
            // codex wraps everything in a payload envelope; messages live in
            // response_item events.
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("type", out var itemType)
                || itemType.GetString() != "message")
            {
                return null;
            }

            var role = payload.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
            if (role is not ("user" or "assistant"))
                return null;

            var codexText = ExtractText(payload);
            return string.IsNullOrWhiteSpace(codexText) ? null : (role == "user", codexText);
        }

        if (!root.TryGetProperty("message", out var message))
            return null;

        var messageRole = "";
        if (format == Format.ClaudeCode)
        {
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() is not ("user" or "assistant"))
                return null;
            messageRole = message.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
        }
        else
        {
            messageRole = root.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
        }

        if (messageRole is not ("user" or "assistant"))
            return null;

        var text = ExtractText(message);
        return string.IsNullOrWhiteSpace(text) ? null : (messageRole == "user", text);
    }
}
