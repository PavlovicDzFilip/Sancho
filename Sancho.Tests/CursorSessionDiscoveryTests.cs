using System.Text.Json;
using Sancho.Console.Agents;
using Xunit;

namespace Sancho.Tests;

public sealed class CursorSessionDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sancho-cursor-discovery-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_root, "target");

    private void Metadata(string workspace, string id, string content)
    {
        var directory = Path.Combine(_root, "chats", workspace, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "meta.json"), content);
    }

    private string Chat(string cwd, bool hasConversation, long createdAtMs, long? updatedAtMs = null) =>
        JsonSerializer.Serialize(new { cwd, hasConversation, createdAtMs, updatedAtMs });

    [Fact]
    public void NativeChatsRequireMatchingWorkspaceAndConversation()
    {
        Metadata("hash", "valid", Chat(Target + Path.DirectorySeparatorChar, true, 1000, 3000));
        Metadata("hash", "wrong", Chat(Target + "-other", true, 1000, 5000));
        Metadata("hash", "empty", Chat(Target, false, 1000, 5000));
        Metadata("hash", "broken", "{broken");
        Metadata("hash", "array", "[]");
        Metadata("hash", "invalid-time", Chat(Target, true, 1000, long.MaxValue));
        var session = Assert.Single(CursorAgentService.ListSessions(_root, Target));
        Assert.Equal("valid", session.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(3000).LocalDateTime, session.LastActivity);
    }

    [Fact]
    public void MergesIdeTranscriptsAndDeduplicatesNewestMetadata()
    {
        Metadata("hash1", "shared", Chat(Target, true, 1000, 2000));
        Metadata("hash2", "shared", Chat(Target, true, 1000, 4000));
        Metadata("hash1", "created-only", JsonSerializer.Serialize(new { cwd = Target, hasConversation = true, createdAtMs = 3000 }));
        var transcripts = Path.Combine(_root, "projects", SessionJson.EncodeProjectDirectory(Target), "agent-transcripts");
        var shared = Path.Combine(transcripts, "shared");
        var ide = Path.Combine(transcripts, "ide-only");
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(ide);
        Directory.SetLastWriteTime(shared, DateTimeOffset.FromUnixTimeMilliseconds(1000).LocalDateTime);
        Directory.SetLastWriteTime(ide, DateTimeOffset.FromUnixTimeMilliseconds(2000).LocalDateTime);
        var sessions = CursorAgentService.ListSessions(_root, Target);
        Assert.Equal(new[] { "shared", "created-only", "ide-only" }, sessions.Select(session => session.Id));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(4000).LocalDateTime, sessions[0].LastActivity);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
