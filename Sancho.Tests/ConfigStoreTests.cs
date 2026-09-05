using Sancho.Console.Config;
using Xunit;

namespace Sancho.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void WithKey_Model_StoresValue()
    {
        var config = ConfigStore.WithKey(new SanchoConfig(), "model", "tiny");

        Assert.Equal("tiny", config.Model);
        Assert.Null(config.Agent);
    }

    [Fact]
    public void GetValue_Model_ReadsBack()
    {
        var config = new SanchoConfig(Model: "medium");

        Assert.Equal("medium", ConfigStore.GetValue(config, "model"));
    }

    [Fact]
    public void WithKey_Model_EmptyClears()
    {
        var config = ConfigStore.WithKey(new SanchoConfig(Model: "tiny"), "model", "");

        Assert.Null(config.Model);
    }

    [Fact]
    public void KnownKeys_ContainsAgentAndModel()
    {
        Assert.Contains("agent", ConfigStore.KnownKeys);
        Assert.Contains("model", ConfigStore.KnownKeys);
    }

    [Fact]
    public void WithKey_UnknownKeyThrows()
    {
        var ex = Assert.Throws<ConfigException>(
            () => ConfigStore.WithKey(new SanchoConfig(), "nope", "x"));

        Assert.Contains("agent", ex.Message);
        Assert.Contains("model", ex.Message);
    }
}
