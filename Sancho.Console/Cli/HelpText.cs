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
        Sancho — voice assistant: microphone → transcription → Claude CLI.

        Usage:
          sancho                          Start listening in the current directory
          sancho --continue               Resume one of your previous sessions (alias: -c)
          sancho config get               Show config (apiKey is masked; 'config get apiKey' shows it raw)
          sancho config set <key> <value> Persist one config key

        Options:
          --api-key <key>                 OpenAI API key (this run only; not persisted)
          --transcription <mode>          openai (default), record or local — this run only, not persisted
          --log                            Write a run log (console mirror + debug diagnostics + claude I/O)
                                           to ~/.sancho/sancho.log — this run only, not persisted
          -h, --help                      Show this help
          -v, --version                   Show version

        Prompt:
          .sancho.md in the current directory

        Local (--transcription local):
          On-device speech-to-text via sherpa-onnx — your voice never leaves the machine.
          Utterances arrive when you stop speaking (~1 s after).
          First run downloads the speech model (~380 MB) into ~/.sancho/models.

        Recording (--transcription record):
          Files: ~/.sancho/recordings     (WAV; SANCHO_CONFIG_DIR overrides the directory)
          In record mode, Claude won't hear your voice.

        Config:
          File:  ~/.sancho/config.json    (SANCHO_CONFIG_DIR overrides the directory)
          Keys:  apiKey                   (prompted in openai mode; used for session titles)
                 transcription            (openai | record | local; default: openai)
          Precedence: config file < OPENAI_API_KEY env var < --api-key flag
        """;
}
