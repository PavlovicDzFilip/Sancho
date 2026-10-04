using Sancho.Console.Audio;
using Xunit;

namespace Sancho.Tests;

public class LinuxLoopbackDevicesTests
{
    [Fact]
    public void ChoosesActualMonitorOfDefaultOutputRatherThanMicrophoneOrOtherSink()
    {
        const string sinks = """
            [{"name":"other-output","monitor_source_name":"other.monitor"},
             {"name":"selected-output","monitor_source_name":"actual-system-monitor"}]
            """;
        Assert.Equal("actual-system-monitor", LinuxLoopbackDevices.SelectMonitor(sinks, "selected-output"));
    }

    [Fact]
    public void SupportsPipeWirePactlMonitorSourceString() =>
        Assert.Equal("headset.monitor", LinuxLoopbackDevices.SelectMonitor(
            "[{\"name\":\"headset\",\"monitor_source\":\"headset.monitor\"}]", "headset"));

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"name\":\"selected-output\"}]")]
    [InlineData("[{\"name\":\"selected-output\",\"monitor_source_name\":\"\"}]")]
    [InlineData("[{\"name\":\"other-output\",\"monitor_source_name\":\"other.monitor\"}]")]
    public void MissingMonitorFailsWithoutMicrophoneFallback(string sinks) =>
        Assert.Throws<InvalidOperationException>(() => LinuxLoopbackDevices.SelectMonitor(sinks, "selected-output"));
}
