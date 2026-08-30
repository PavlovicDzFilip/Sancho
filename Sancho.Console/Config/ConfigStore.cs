using System.Text.Json;

namespace Sancho.Console.Config;

/// <summary>Raised when the user config file is invalid or a key is unknown.</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>Loads and saves Sancho's user config (<c>~/.sancho/config.json</c>).</summary>
public static class ConfigStore
{
    /// <summary>Keys accepted by <c>sancho config get/set</c>.</summary>
    public static readonly string[] KnownKeys = ["apiKey"];

    /// <summary>
    /// Loads the config file. A missing file yields defaults; malformed JSON
    /// fails fast with the file path instead of silently resetting.
    /// </summary>
    public static SanchoConfig Load()
    {
        var path = SanchoPaths.ConfigFile;
        if (!File.Exists(path))
            return new SanchoConfig();

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), SanchoConfigJsonContext.Default.SanchoConfig)
                ?? new SanchoConfig();
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Invalid config file '{path}': {ex.Message}");
        }
    }

    /// <summary>Writes the config file atomically, creating the directory if needed.</summary>
    public static void Save(SanchoConfig config)
    {
        var path = SanchoPaths.ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = JsonSerializer.Serialize(config, SanchoConfigJsonContext.Default.SanchoConfig);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Returns a copy of <paramref name="config"/> with one key set.</summary>
    /// <remarks>An empty value clears the key (stores <c>null</c>).</remarks>
    public static SanchoConfig WithKey(SanchoConfig config, string key, string? value) => key switch
    {
        "apiKey" => config with { ApiKey = EmptyToNull(value) },
        _ => throw new ConfigException($"Unknown config key '{key}'. Valid keys: {string.Join(", ", KnownKeys)}."),
    };

    /// <summary>Reads one key from <paramref name="config"/>.</summary>
    public static string? GetValue(SanchoConfig config, string key) => key switch
    {
        "apiKey" => config.ApiKey,
        _ => throw new ConfigException($"Unknown config key '{key}'. Valid keys: {string.Join(", ", KnownKeys)}."),
    };

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
