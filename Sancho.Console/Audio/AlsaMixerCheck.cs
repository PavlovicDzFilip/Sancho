using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sancho.Console.Audio;

/// <summary>
/// Reads the ALSA capture-mute state on Linux by shelling out to amixer.
/// The physical mic-mute button on many laptops toggles the mixer's Capture
/// switch, so <c>[off]</c> there means the mic is muted. Non-Linux platforms
/// and machines without amixer get an empty result — the live
/// <see cref="MicLevelMonitor"/> is the cross-platform fallback.
/// </summary>
public static class AlsaMixerCheck
{
    private const int MaxCards = 8;

    /// <summary>Card labels ("card 1", …) whose Capture switch is muted.</summary>
    public static IReadOnlyList<string> FindMutedCaptureControls(ILogger logger)
    {
        if (!OperatingSystem.IsLinux())
            return Array.Empty<string>();

        var muted = new List<string>();
        for (var card = 0; card < MaxCards; card++)
        {
            var output = RunAmixer($"-c {card} sget Capture", logger);
            if (output is null)
                continue; // no amixer, or no such card/control

            if (output.Any(line => line.Contains("[off]", StringComparison.Ordinal)))
                muted.Add($"card {card}");
        }

        return muted;
    }

    private static string[]? RunAmixer(string args, ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo("amixer", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0)
                return null;

            var lines = new List<string>();
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
                lines.Add(line);
            return lines.ToArray();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            logger.LogDebug("amixer not found — skipping the ALSA mute check");
            return null;
        }
    }
}
