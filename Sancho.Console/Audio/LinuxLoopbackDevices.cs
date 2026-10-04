using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Sancho.Console.Audio;

internal static class LinuxLoopbackDevices
{
    public static string ResolveDefaultSinkMonitor()
    {
        try
        {
            var sink = RunPactl("get-default-sink").Trim();
            return SelectMonitor(RunPactl("--format=json", "list", "sinks"), sink);
        }
        catch (Exception ex) when (ex is Win32Exception or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Meeting mode needs a PulseAudio/PipeWire output monitor. Ensure your desktop audio server is running and pactl is installed (pulseaudio-utils). " + ex.Message, ex);
        }
    }

    internal static string SelectMonitor(string sinksJson, string defaultSink)
    {
        using var document = JsonDocument.Parse(sinksJson);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var sink in document.RootElement.EnumerateArray())
            {
                if (sink.ValueKind != JsonValueKind.Object
                    || !sink.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || name.GetString() != defaultSink) continue;
                // pactl versions expose either monitor_source_name or a string monitor_source.
                var hasMonitor = sink.TryGetProperty("monitor_source_name", out var monitor)
                    || sink.TryGetProperty("monitor_source", out monitor);
                if (hasMonitor
                    && monitor.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(monitor.GetString()))
                    return monitor.GetString()!;
            }
        }
        throw new InvalidOperationException("The default output has no available monitor source; microphone capture cannot substitute for system audio.");
    }

    private static string RunPactl(params string[] arguments)
    {
        var info = new ProcessStartInfo("pactl")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("pactl could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("Audio monitor discovery timed out.");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Result.Trim());
        return output.Result;
    }
}
