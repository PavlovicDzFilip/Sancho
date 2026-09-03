namespace Sancho.Console.Config;

/// <summary>
/// Locations of Sancho's per-user files. Everything lives in
/// <c>~/.sancho</c>, unless <c>SANCHO_CONFIG_DIR</c> points elsewhere.
/// </summary>
public static class SanchoPaths
{
    public const string ConfigDirEnvVar = "SANCHO_CONFIG_DIR";

    /// <summary>The per-user config directory.</summary>
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable(ConfigDirEnvVar) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sancho");

    /// <summary>Path to <c>config.json</c>.</summary>
    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    /// <summary>Directory where voice recordings are written.</summary>
    public static string RecordingsDir => Path.Combine(ConfigDir, "recordings");

    /// <summary>Run log written when <c>--log</c> is passed.</summary>
    public static string LogFile => Path.Combine(ConfigDir, "sancho.log");

    /// <summary>Directory where local speech-to-text models are downloaded.</summary>
    public static string ModelsDir => Path.Combine(ConfigDir, "models");
}
