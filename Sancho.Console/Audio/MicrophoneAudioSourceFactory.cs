using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Spectre.Console;

namespace Sancho.Console.Audio;

/// <summary>
/// Selects a microphone device using Spectre.Console and creates
/// a configured <see cref="MicrophoneAudioSource"/>.
/// </summary>
public sealed class MicrophoneAudioSourceFactory(
    ILoggerFactory loggerFactory,
    Display display)
{
    public MicrophoneAudioSource Create()
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

            prompt.UseConverter(i =>  choices[i]);

            selected = AnsiConsole.Prompt(prompt);
        }

        var deviceName = WaveInEvent.GetCapabilities(selected).ProductName;

        return new MicrophoneAudioSource(selected, deviceName,
            loggerFactory.CreateLogger<MicrophoneAudioSource>(),
            display);
    }
}