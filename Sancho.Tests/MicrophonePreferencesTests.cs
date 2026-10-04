using System.Text.Json;
using Sancho.Console.Audio;
using Sancho.Console.Cli;
using Sancho.Console.Config;
using Xunit;

namespace Sancho.Tests;

public class MicrophonePreferencesTests
{
    [Fact]
    public void UnpluggedPreferredHeadsetFallsBackWithoutReorderingAndReturnsWhenAvailable()
    {
        var priority = new[] { "headset", "internal" };
        var fallback = MicrophonePreferences.Choose(["internal"], priority, false, true,
            () => throw new Exception("No prompt expected"), out var remember);
        Assert.Equal(0, fallback);
        Assert.False(remember);
        var returned = MicrophonePreferences.Choose(["internal", "headset"], priority, false, true,
            () => throw new Exception("No prompt expected"), out remember);
        Assert.Equal(1, returned);
        Assert.False(remember);
        Assert.Equal(new[] { "headset", "internal" }, priority);
    }

    [Fact]
    public void ExplicitChoiceOverridesRememberedPriorityAndPromotesWithoutDuplicates()
    {
        var priority = new[] { "headset", "internal", "usb" };
        var selected = MicrophonePreferences.Choose(["headset", "internal"], priority, true, true,
            () => 1, out var remember);
        Assert.Equal(1, selected);
        Assert.True(remember);
        Assert.Equal(new[] { "internal", "headset", "usb" }, MicrophonePreferences.Promote(priority, "internal"));
    }

    [Fact]
    public void InitialMultipleDevicesPromptsButRecoveryNeverPromptsOrPersists()
    {
        Assert.Equal(1, MicrophonePreferences.Choose(["a", "b"], null, false, true, () => 1, out var remember));
        Assert.True(remember);
        Assert.Equal(0, MicrophonePreferences.Choose(["a", "b"], null, false, false,
            () => throw new Exception("Recovery must not prompt"), out remember));
        Assert.False(remember);
        Assert.Equal(0, MicrophonePreferences.Choose(["new"], ["absent"], false, true,
            () => throw new Exception("Fallback must not prompt"), out remember));
        Assert.False(remember);
    }

    [Fact]
    public void InitialSingleDeviceIsRememberedWithoutPromptAndForcedChoiceAlwaysPrompts()
    {
        Assert.Equal(0, MicrophonePreferences.Choose(["single"], null, false, true,
            () => throw new Exception("Unexpected prompt"), out var remember));
        Assert.True(remember);
        var prompted = false;
        MicrophonePreferences.Choose(["single"], ["single"], true, true, () => { prompted = true; return 0; }, out remember);
        Assert.True(prompted);
        Assert.True(remember);
    }

    [Fact]
    public void MissingDevicesAndInvalidSelectionsFailClearly()
    {
        Assert.Throws<InvalidOperationException>(() => MicrophonePreferences.Choose([], null, false, true, () => 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => MicrophonePreferences.Choose(["a"], null, true, true, () => 2, out _));
    }

    [Fact]
    public void ConfigRoundTripsPriorityAndLegacyConfigPreservesDefaults()
    {
        var legacy = JsonSerializer.Deserialize("{\"agent\":\"codex\",\"model\":\"tiny\"}", SanchoConfigJsonContext.Default.SanchoConfig)!;
        Assert.Null(legacy.MicrophonePriority);
        var config = legacy with { MicrophonePriority = ["linux:headset:", "linux:internal:port"] };
        var json = JsonSerializer.Serialize(config, SanchoConfigJsonContext.Default.SanchoConfig);
        var saved = JsonSerializer.Deserialize(json, SanchoConfigJsonContext.Default.SanchoConfig)!;
        Assert.Equal(config.MicrophonePriority, saved.MicrophonePriority);
        Assert.Equal("codex", saved.Agent);
        Assert.Equal(config.MicrophonePriority, ConfigStore.WithKey(saved, "model", "small").MicrophonePriority);
    }

    [Fact]
    public void SelectionFlagIsRunSpecificAndWorksWithOtherModes()
    {
        var args = CliArgs.Parse(["--select-microphone", "--notes", "--agent", "dummy"]);
        Assert.True(args.SelectMicrophone);
        Assert.True(args.Notes);
        Assert.Equal("dummy", args.Agent);
        Assert.False(CliArgs.Parse([]).SelectMicrophone);
        Assert.Contains("--select-microphone", HelpText.Body);
    }
}
