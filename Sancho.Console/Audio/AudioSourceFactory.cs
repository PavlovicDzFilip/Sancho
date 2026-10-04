using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Spectre.Console;

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
    MicLevelMonitor micMonitor)
{
    public IAudioSource Create()
    {
        if (OperatingSystem.IsWindows())
            return CreateWindowsSource();
        if (OperatingSystem.IsMacOS())
            return CreateMacOsSource();
        if (OperatingSystem.IsLinux())
            return CreateLinuxSource();

        throw new PlatformNotSupportedException("Audio capture is not supported on this platform.");
    }

    /// <summary>
    /// Creates the loopback source capturing the system audio output (other
    /// meeting participants). Windows-only for now — Linux pulse monitors and
    /// macOS virtual devices are future work.
    /// </summary>
    public IAudioSource CreateLoopback()
    {
        if (OperatingSystem.IsWindows())
            return new LoopbackAudioSource(loggerFactory.CreateLogger<LoopbackAudioSource>(), display);

        throw new PlatformNotSupportedException(
            "Loopback capture is Windows-only for now — Linux/macOS coming later.");
    }

    private IAudioSource CreateWindowsSource()
    {
        var count = WaveInEvent.DeviceCount;

        if (count == 0)
            throw new InvalidOperationException(
                "No recording devices found. Plug in a microphone and try again.");

        int selected;

        if (count == 1)
        {
            selected = 0;
        }
        else
        {
            var choices = Enumerable.Range(0, count)
                .Select(i => WaveInEvent.GetCapabilities(i).ProductName)
                .ToList();

            var prompt = new SelectionPrompt<int>()
                .Title("🎤 Multiple microphones found. Select one:")
                .AddChoices(choices.Select((_, i) => i));

            prompt.UseConverter(i => choices[i]);

            selected = AnsiConsole.Prompt(prompt);
        }

        var deviceName = WaveInEvent.GetCapabilities(selected).ProductName;

        return new MicrophoneAudioSource(selected, deviceName,
            loggerFactory.CreateLogger<MicrophoneAudioSource>(),
            display, micMonitor);
    }

    private IAudioSource CreateMacOsSource()
    {
        var devices = FfmpegDevices.ListAvFoundationAudioDevices();
        var device = PickDevice(devices);
        return new FfmpegAudioSource(
            $"Using device [{device.Index}]: {device.Name}",
            ["-f", "avfoundation", "-i", $":{device.Index}"],
            fallbackInputArgs: null,
            loggerFactory.CreateLogger<FfmpegAudioSource>(),
            display, micMonitor);
    }

    private IAudioSource CreateLinuxSource()
    {
        var choice = LinuxAudioDevices.SelectMicrophone(LinuxAudioDevices.Discover(), devices =>
        {
            var prompt = new SelectionPrompt<int>()
                .Title("ðŸŽ¤ Multiple microphones found. Select one:")
                .AddChoices(Enumerable.Range(0, devices.Count));
            prompt.UseConverter(index => Markup.Escape($"{devices[index].Description} ({devices[index].Name})"));
            return AnsiConsole.Prompt(prompt);
        });
        return new FfmpegAudioSource(
            choice.Description, choice.InputArguments, choice.FallbackArguments,
            loggerFactory.CreateLogger<FfmpegAudioSource>(),
            display, micMonitor);
    }

    private static FfmpegDevices.Device PickDevice(IReadOnlyList<FfmpegDevices.Device> devices)
    {
        if (devices.Count == 0)
            throw new InvalidOperationException(
                "No recording devices found. Plug in a microphone and try again.");

        if (devices.Count == 1)
            return devices[0];

        var prompt = new SelectionPrompt<int>()
            .Title("🎤 Multiple microphones found. Select one:")
            .AddChoices(devices.Select((_, i) => i));

        prompt.UseConverter(i => devices[i].Name);

        return devices[AnsiConsole.Prompt(prompt)];
    }
}
