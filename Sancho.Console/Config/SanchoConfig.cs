using System.Text.Json.Serialization;

namespace Sancho.Console.Config;

/// <summary>
/// User configuration persisted in <c>~/.sancho/config.json</c>.
/// A <c>null</c> value means "not set"; resolution happens with
/// precedence in Program.cs (defaults &lt; file &lt; env &lt; flags).
/// </summary>
public sealed record SanchoConfig(
    string? ApiKey = null,
    string? Transcription = null);

/// <summary>Source-generated JSON serializer for <see cref="SanchoConfig"/> (AOT-safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SanchoConfig))]
internal partial class SanchoConfigJsonContext : JsonSerializerContext;
