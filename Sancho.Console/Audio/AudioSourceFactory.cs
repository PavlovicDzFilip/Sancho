using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Spectre.Console;
using Sancho.Console.Config;

namespace Sancho.Console.Audio;

/// <summary>
/// Creates the capture source for the current platform: NAudio on Windows
/// (no external dependencies), ffmpeg with the avfoundation backend on macOS
/// (with an interactive device picker), or the default pulse/ALSA device on
/// Linux.
/// </summary>
public sealed class AudioSourceFactory(
    ILoggerFactory loggerFactory,
    Display display,
    MicLevelMonitor micMonitor,
    bool selectMicrophone = false)
{
    public IAudioSource Create()
    {
        // Initial selection may prompt; all subsequent checks are automatic.
        ResolveMicrophone(selectMicrophone);
        return new ResilientAudioSource(() => ResolveMicrophone(allowPrompt: false),
            loggerFactory.CreateLogger<ResilientAudioSource>(),
            selectAvailable: excluded => ResolveMicrophone(allowPrompt: false, excludedIdentities: excluded),
            onDeviceStopped: micMonitor.Reset);
    }

    /// <summary>Enumerate fresh devices. Recovery sets allowPrompt=false and never alters saved priority.</summary>
    public AudioSourceSelection ResolveMicrophone(bool forceSelection = false, bool allowPrompt = true,
        IReadOnlySet<string>? excludedIdentities = null)
    {
        if (OperatingSystem.IsWindows())
            return ResolveWindowsSource(forceSelection, allowPrompt, excludedIdentities);
        if (OperatingSystem.IsMacOS())
            return ResolveMacOsSource(forceSelection, allowPrompt, excludedIdentities);
        if (OperatingSystem.IsLinux())
            return ResolveLinuxSource(forceSelection, allowPrompt, excludedIdentities);

        throw new PlatformNotSupportedException("Audio capture is not supported on this platform.");
    }

    /// <summary>
    /// Creates the loopback source capturing the system audio output (other
    /// meeting participants), using WASAPI on Windows or the default output's
    /// PulseAudio/PipeWire monitor on Linux.
    /// </summary>
    public IAudioSource CreateLoopback()
    {
        AudioSourceSelection ResolveOutput()
        {
            if (OperatingSystem.IsWindows())
            {
                using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var output = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                return new(output.ID, "System output: " + output.FriendlyName,
                    () => new LoopbackAudioSource(loggerFactory.CreateLogger<LoopbackAudioSource>(), display));
            }
            if (OperatingSystem.IsLinux())
            {
                var monitor = LinuxLoopbackDevices.ResolveDefaultSinkMonitor();
                return new(monitor, $"System output: {monitor}", () => new FfmpegAudioSource($"System output: {monitor}",
                    ["-f", "pulse", "-i", monitor], fallbackInputArgs: null,
                    loggerFactory.CreateLogger<FfmpegAudioSource>(), display, micMonitor: null));
            }
            throw new PlatformNotSupportedException(
                "Meeting mode system audio capture is supported on Windows and Linux; macOS requires a virtual audio device and is not supported yet.");
        }
        ResolveOutput(); // Initial preflight remains a clear startup error.
        return new ResilientAudioSource(() =>
        {
            try { return ResolveOutput(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        }, loggerFactory.CreateLogger<ResilientAudioSource>());
    }

    private AudioSourceSelection ResolveWindowsSource(bool forceSelection, bool allowPrompt, IReadOnlySet<string>? excludedIdentities)
    {
        var count = WaveInEvent.DeviceCount;
        var devices = Enumerable.Range(0, count).Select(index => (Index: index, Capabilities: WaveInEvent.GetCapabilities(index)))
            .Where(device => excludedIdentities?.Contains(WindowsIdentity(device.Capabilities)) != true).ToArray();
        // WaveIn does not expose endpoint GUIDs. Avoid volatile device indexes:
        // product/manufacturer/channels is the best identity its API provides.
        var ids = devices.Select(d => WindowsIdentity(d.Capabilities)).ToArray();
        var selected = Select(ids, devices.Select(d => d.Capabilities.ProductName).ToArray(), forceSelection, allowPrompt);
        var deviceName = devices[selected].Capabilities.ProductName;
        return new(ids[selected], deviceName, () => new MicrophoneAudioSource(devices[selected].Index, deviceName,
            loggerFactory.CreateLogger<MicrophoneAudioSource>(), display, micMonitor));
    }

    private AudioSourceSelection ResolveMacOsSource(bool forceSelection, bool allowPrompt, IReadOnlySet<string>? excludedIdentities)
    {
        var devices = FfmpegDevices.ListAvFoundationAudioDevices()
            .Where(device => excludedIdentities?.Contains("macos:" + Uri.EscapeDataString(device.Name)) != true).ToArray();
        var ids = devices.Select(d => "macos:" + Uri.EscapeDataString(d.Name)).ToArray();
        var selected = Select(ids, devices.Select(d => d.Name).ToArray(), forceSelection, allowPrompt);
        var device = devices[selected];
        return new(ids[selected], device.Name, () => new FfmpegAudioSource(
            $"Using device [{device.Index}]: {device.Name}",
            ["-f", "avfoundation", "-i", $":{device.Index}"],
            fallbackInputArgs: null,
            loggerFactory.CreateLogger<FfmpegAudioSource>(),
            display, micMonitor));
    }

    private AudioSourceSelection ResolveLinuxSource(bool forceSelection, bool allowPrompt, IReadOnlySet<string>? excludedIdentities)
    {
        var sources = LinuxAudioDevices.Discover();
        if (sources is null)
        {
            if (forceSelection) throw new InvalidOperationException("Microphone selection requires pactl and a running PulseAudio/PipeWire server. Install pulseaudio-utils and try again.");
            if (excludedIdentities?.Contains("linux:default") == true)
                throw new InvalidOperationException("Default microphone is temporarily unavailable.");
            var fallback = LinuxAudioDevices.SelectMicrophone(null, _ => 0);
            return new("linux:default", fallback.Description, () => CreateFfmpeg(fallback));
        }
        var devices = sources.Where(source => !source.IsMonitor && excludedIdentities?.Contains(LinuxIdentity(source)) != true).ToArray();
        var ids = devices.Select(LinuxIdentity).ToArray();
        var selected = Select(ids, devices.Select(d => $"{d.Description} ({d.Name})").ToArray(), forceSelection, allowPrompt);
        var device = devices[selected];
        return new(ids[selected], device.Description, () =>
        {
            // Activate an analog port only when capture starts, not while polling availability.
            var choice = LinuxAudioDevices.SelectMicrophone([device], _ => 0);
            return CreateFfmpeg(choice);
        });
    }

    private FfmpegAudioSource CreateFfmpeg(LinuxAudioDevices.CaptureChoice choice) =>
        new(choice.Description, choice.InputArguments, choice.FallbackArguments,
            loggerFactory.CreateLogger<FfmpegAudioSource>(), display, micMonitor);

    private static string WindowsIdentity(WaveInCapabilities device) =>
        $"windows:{Uri.EscapeDataString(device.ProductName)}:{device.ManufacturerGuid}:{device.Channels}";

    private static string LinuxIdentity(LinuxAudioDevices.Source source) =>
        $"linux:{Uri.EscapeDataString(source.Name)}:{Uri.EscapeDataString(source.Port ?? "")}";

    private static int Select(string[] ids, string[] descriptions, bool forceSelection, bool allowPrompt)
    {
        var config = ConfigStore.Load();
        var selected = MicrophonePreferences.Choose(ids, config.MicrophonePriority,
            forceSelection, allowPrompt, () =>
            {
                var prompt = new SelectionPrompt<int>()
                    .Title("🎤 Select a microphone:")
                    .AddChoices(Enumerable.Range(0, ids.Length));
                prompt.UseConverter(index => Markup.Escape(descriptions[index]));
                return AnsiConsole.Prompt(prompt);
            }, out var remember);
        if (remember) ConfigStore.RememberMicrophone(ids[selected]);
        return selected;
    }
}
