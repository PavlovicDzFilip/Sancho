using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Sancho.Console.Audio;

/// <summary>
/// Lists capture devices by invoking ffmpeg's platform-specific listers.
/// ffmpeg prints device lists to stderr and exits with a nonzero code on
/// Windows, so enumeration parses the output rather than trusting exit codes.
/// </summary>
public static class FfmpegDevices
{
    /// <summary>A named capture device; <see cref="Device.Index"/> is only meaningful for avfoundation.</summary>
    public sealed record Device(string Name, int Index);

    /// <summary>Audio devices available to DirectShow (Windows).</summary>
    public static IReadOnlyList<Device> ListDshowAudioDevices()
    {
        var lines = RunLister("-list_devices", "true", "-f", "dshow", "-i", "dummy");

        var devices = new List<Device>();
        foreach (var line in lines)
        {
            var match = Regex.Match(line, "\"([^\"]+)\"\\s*\\(audio\\)");
            if (match.Success)
                devices.Add(new Device(match.Groups[1].Value, devices.Count));
        }

        return devices.DistinctBy(d => d.Name).ToList();
    }

    /// <summary>Audio devices available to AVFoundation (macOS).</summary>
    public static IReadOnlyList<Device> ListAvFoundationAudioDevices()
    {
        var lines = RunLister("-list_devices", "true", "-f", "avfoundation", "-i", "");

        var devices = new List<Device>();
        var inAudioSection = false;
        foreach (var line in lines)
        {
            if (line.Contains("AVFoundation audio devices:", StringComparison.Ordinal))
            {
                inAudioSection = true;
                continue;
            }

            if (!inAudioSection)
                continue;

            var match = Regex.Match(line, "\\[(\\d+)\\]\\s+(.+?)\\s*$");
            if (!match.Success)
                break; // audio section ended

            devices.Add(new Device(match.Groups[2].Value.Trim(), int.Parse(match.Groups[1].Value)));
        }

        return devices;
    }

    /// <summary>
    /// Runs an ffmpeg list-devices invocation and returns every line it
    /// printed (stderr and stdout). Throws when ffmpeg is missing, times
    /// out, or cannot be started.
    /// </summary>
    private static string[] RunLister(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("ffmpeg could not be started.");

            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Device listing timed out.");
            }

            // Read after exit so the streams can never deadlock the process.
            var lines = new List<string>();
            string? line;
            while ((line = process.StandardError.ReadLine()) is not null)
                lines.Add(line);
            while ((line = process.StandardOutput.ReadLine()) is not null)
                lines.Add(line);

            return lines.ToArray();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "ffmpeg was not found. Install it (the installer script does this automatically) and try again.");
        }
    }
}
