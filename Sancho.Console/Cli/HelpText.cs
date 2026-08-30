namespace Sancho.Console.Cli;

/// <summary>Static help and version text for the CLI surface.</summary>
public static class HelpText
{
    /// <summary>
    /// Shown by <c>--version</c>. A compile-time constant because assembly
    /// version metadata is stripped under NativeAOT.
    /// </summary>
    public const string Version = "0.1.0";

    /// <summary>Body shown by <c>--help</c>.</summary>
    public const string Body = """
        Sancho — live voice assistant: microphone → transcription → Claude CLI.

        Usage:
          sancho                          Start listening in the current directory
          sancho --continue               Resume one of your previous sessions (alias: -c)
          sancho config get               Show config (apiKey is masked; 'config get apiKey' shows it raw)
          sancho config set <key> <value> Persist one config key

        Options:
          --api-key <key>                 OpenAI API key (this run only; not persisted)
          -h, --help                      Show this help
          -v, --version                   Show version

        Prompt:
          .sancho.md in the current directory

        Config:
          File:  ~/.sancho/config.json    (SANCHO_CONFIG_DIR overrides the directory)
          Key:   apiKey
          Precedence: config file < OPENAI_API_KEY env var < --api-key flag
        """;
}
