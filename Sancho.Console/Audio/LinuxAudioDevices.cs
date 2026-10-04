using System.Diagnostics;
using System.Text.Json;

namespace Sancho.Console.Audio;

/// <summary>PulseAudio/PipeWire source discovery. Unavailable discovery is distinct from an empty device list.</summary>
internal static class LinuxAudioDevices
{
    internal sealed record Source(string Name, string Description, bool IsMonitor, string? Port = null);
    internal sealed record CaptureChoice(string Description, string[] InputArguments, string[]? FallbackArguments);

    internal static IReadOnlyList<Source>? Discover()
    {
        try
        {
            var output = RunPactl("--format=json", "list", "sources");
            return output is null ? null : ParseSources(output);
        }
        catch (JsonException) { return null; }
    }

    internal static string? RunPactl(params string[] arguments)
    {
        var psi = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return null;
            }
            var output = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    internal static IReadOnlyList<Source> ParseSources(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("PulseAudio sources must be an array.");
        var sources = new List<Source>();
        foreach (var source in document.RootElement.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object) continue;
            var name = Text(source, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var monitor = name.EndsWith(".monitor", StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(Text(source, "monitor_of_sink_name"))
                || !string.IsNullOrWhiteSpace(Text(source, "monitor_source"));
            if (source.TryGetProperty("monitor_of_sink", out var sink))
                monitor |= sink.ValueKind == JsonValueKind.Number && sink.TryGetUInt32(out var index) && index != uint.MaxValue;
            var description = Text(source, "description");
            if (source.TryGetProperty("properties", out var properties))
            {
                monitor |= Text(properties, "device.class") == "monitor";
                if (string.IsNullOrWhiteSpace(description)) description = Text(properties, "device.description");
            }
            description = string.IsNullOrWhiteSpace(description) ? name : description;
            if (!monitor && source.TryGetProperty("ports", out var ports)
                && ports.ValueKind == JsonValueKind.Array && ports.GetArrayLength() > 0)
            {
                foreach (var port in ports.EnumerateArray())
                {
                    var portName = Text(port, "name");
                    var availability = Text(port, "availability");
                    if (string.IsNullOrWhiteSpace(portName) || availability is "not available" or "no") continue;
                    sources.Add(new Source(name, $"{description} — {Text(port, "description") ?? portName}", false, portName));
                }
            }
            else sources.Add(new Source(name, description, monitor));
        }
        return sources.DistinctBy(source => (source.Name, source.Port)).ToArray();
    }

    internal static CaptureChoice SelectMicrophone(IReadOnlyList<Source>? sources, Func<IReadOnlyList<Source>, int> picker,
        Action<Source>? selectPort = null)
    {
        if (sources is null)
            return new("Microphone listing unavailable; using default microphone (PulseAudio, ALSA fallback)",
                ["-f", "pulse", "-i", "default"], ["-f", "alsa", "-i", "default"]);
        var microphones = sources.Where(source => !source.IsMonitor).ToArray();
        if (microphones.Length == 0)
            throw new InvalidOperationException("No recording devices found. Plug in a microphone and try again.");
        var selected = microphones.Length == 1 ? 0 : picker(microphones);
        if ((uint)selected >= microphones.Length) throw new ArgumentOutOfRangeException(nameof(picker));
        var microphone = microphones[selected];
        if (microphone.Port is not null)
        {
            if (selectPort is not null) selectPort(microphone);
            else if (RunPactl("set-source-port", microphone.Name, microphone.Port) is null)
                throw new InvalidOperationException($"Could not activate microphone port: {microphone.Description}. Check your audio settings and try again.");
        }
        // A named selection must never silently record a different default microphone.
        return new($"Using microphone: {microphone.Description}", ["-f", "pulse", "-i", microphone.Name], null);
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
