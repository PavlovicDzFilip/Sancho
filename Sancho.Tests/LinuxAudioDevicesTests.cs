using Sancho.Console.Audio;
using Xunit;

namespace Sancho.Tests;

public class LinuxAudioDevicesTests
{
    [Fact]
    public void LaptopSourcesExcludeMonitorsAndDisconnectedPortsButKeepBluetooth()
    {
        var sources = LinuxAudioDevices.ParseSources("""
            [
              {"name":"alsa_output.monitor","description":"Monitor","monitor_source":"alsa_output"},
              {"name":"alsa_input","description":"Built-in Audio","monitor_source":"","ports":[
                {"name":"internal","description":"Internal Microphone","availability":"availability unknown"},
                {"name":"headset","description":"Headset Microphone","availability":"not available"}]},
              {"name":"bluez_input.address","description":"soundcore Space Q45","ports":[]},
              {"name":"other_monitor","properties":{"device.class":"monitor"}}
            ]
            """);
        var microphones = sources.Where(s => !s.IsMonitor).ToArray();
        Assert.Equal(2, microphones.Length);
        Assert.Equal("internal", microphones[0].Port);
        Assert.Contains("Internal Microphone", microphones[0].Description);
        Assert.Equal("soundcore Space Q45", microphones[1].Description);
        var choice = LinuxAudioDevices.SelectMicrophone(sources, options =>
        {
            Assert.Equal(2, options.Count);
            return 1;
        });
        Assert.Equal(new[] { "-f", "pulse", "-i", "bluez_input.address" }, choice.InputArguments);
        Assert.Null(choice.FallbackArguments);
    }

    [Fact]
    public void SelectingAnalogPortActivatesExactSourceAndPort()
    {
        var sources = LinuxAudioDevices.ParseSources("""
            [{"name":"analog","description":"Built-in","ports":[
              {"name":"internal","description":"Internal","availability":"available"},
              {"name":"headset","description":"Headset","availability":"available"}]}]
            """);
        LinuxAudioDevices.Source? activated = null;
        var choice = LinuxAudioDevices.SelectMicrophone(sources, _ => 1, source => activated = source);
        Assert.Equal("headset", activated!.Port);
        Assert.Equal("analog", activated.Name);
        Assert.Equal("analog", choice.InputArguments.Last());
        Assert.Null(choice.FallbackArguments);
    }

    [Fact]
    public void SingleMicrophoneDoesNotPromptAndMissingDiscoveryRetainsDefaultFallback()
    {
        var sources = LinuxAudioDevices.ParseSources("""[{"name":"usb","monitor_of_sink":4294967295}]""");
        var choice = LinuxAudioDevices.SelectMicrophone(sources, _ => throw new Exception("Unexpected prompt"));
        Assert.Equal("usb", choice.InputArguments.Last());
        var fallback = LinuxAudioDevices.SelectMicrophone(null, _ => throw new Exception("Unexpected prompt"));
        Assert.Equal("default", fallback.InputArguments.Last());
        Assert.Equal("alsa", fallback.FallbackArguments![1]);
        Assert.Contains("listing unavailable", fallback.Description);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"name\":\"speaker.monitor\"}]")]
    [InlineData("[{\"name\":\"input\",\"ports\":[{\"name\":\"headset\",\"availability\":\"not available\"}]}]")]
    public void SuccessfulDiscoveryWithoutMicrophonesFailsClearly(string json)
    {
        Assert.Throws<InvalidOperationException>(() => LinuxAudioDevices.SelectMicrophone(
            LinuxAudioDevices.ParseSources(json), _ => throw new Exception("Unexpected prompt")));
    }
}
