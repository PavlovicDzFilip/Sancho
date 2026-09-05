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
          sancho config get               Show config
          sancho config set <key> <value> Persist one config key

        Options:
          --agent <name>                   Agent backend: claude (default), cursor, hermes or codex
          --model <size>                   Whisper model size: tiny, base, small (default) or medium
          --meeting                        Transcribe your mic + the system output (other meeting
                                           participants) — lines labeled Me/Others; Windows only
          --notes                          Transcribe to sancho-notes-YYYY-MM-DD.md in the current
                                           directory — Claude is not involved
          --log                            Write a run log (console mirror + debug diagnostics + claude I/O)
                                           to ~/.sancho/sancho.log — this run only, not persisted
          -h, --help                      Show this help
          -v, --version                   Show version

        Prompt:
          .sancho.md in the current directory

        Transcription:
          On-device speech-to-text via sherpa-onnx whisper — your voice never
          leaves the machine.
          Utterances arrive when you stop speaking (~1 s after).
          First run downloads the selected model into ~/.sancho/models/ —
          tiny ~105 MB, base ~140 MB, small ~380 MB, medium ~945 MB. Each size
          keeps its own directory, so switching sizes downloads the new one.

        Config:
          File:  ~/.sancho/config.json    (SANCHO_CONFIG_DIR overrides the directory)
          Keys:  agent                    (claude | cursor | hermes | codex)
                 model                    (tiny | base | small | medium)
        """;
}
