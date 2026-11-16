using System.CommandLine;

namespace Sancho.Console.Cli;

/// <summary>Generated command help, supplemental usage notes, and the public version.</summary>
public static class HelpText
{
    /// <summary>
    /// Shown by <c>--version</c>. A compile-time constant because assembly
    /// version metadata is stripped under NativeAOT.
    /// </summary>
    public const string Version = "0.1.0";

    public static string Render(params string[] commandPath)
    {
        using var output = new StringWriter();
        CliSchema.CreateCommand().Parse([.. commandPath, "--help"], CliSchema.ParserConfiguration())
            .Invoke(new InvocationConfiguration { Output = output, Error = output });
        return output.ToString() + (commandPath.Length == 0 ? Environment.NewLine + Notes + Environment.NewLine : "");
    }

    private const string Notes = """
        Agent arguments:
          Everything after -- is passed directly to the selected agent, unchanged.
          Permission and sandbox flags are caller-controlled.

        Prompt:
          .sancho.md in the working directory selected by --dir

        Example:
          sancho --agent claude --dir ./project -- --dangerously-skip-permissions

        Transcription:
          On-device speech-to-text via sherpa-onnx whisper — your voice never
          leaves the machine.
          Utterances arrive when you stop speaking (~1 s after).
          The selected model downloads at startup (when missing) into
          ~/.sancho/models/ — tiny ~105 MB, base ~140 MB, small ~380 MB,
          medium ~945 MB. Each size keeps its own directory, so switching
          sizes downloads the new one.

        Config:
          File:  ~/.sancho/config.json    (SANCHO_CONFIG_DIR overrides the directory)
          Keys:  agent                    (claude | cursor | hermes | codex | dummy; auto-detected when unset)
                 model                    (tiny | base | small | medium)

        Linux capture needs ffmpeg; microphone selection and meeting mode need
        PulseAudio/PipeWire and pactl (pulseaudio-utils). macOS meeting capture
        is not supported. Start without options to listen in the current directory.
        """;
}
