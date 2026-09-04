using System.Text.Json.Serialization;

namespace Sancho.Console.Config;

/// <summary>
/// User configuration persisted in <c>~/.sancho/config.json</c>.
/// Empty for now — reserved for future settings.
/// </summary>
public sealed record SanchoConfig();

/// <summary>Source-generated JSON serializer for <see cref="SanchoConfig"/> (AOT-safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SanchoConfig))]
internal partial class SanchoConfigJsonContext : JsonSerializerContext;
