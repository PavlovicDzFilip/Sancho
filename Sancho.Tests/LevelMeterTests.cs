using System.Text.RegularExpressions;
using Sancho.Console.Orchestration;
using Xunit;

namespace Sancho.Tests;

public class LevelMeterTests
{
    private const string Green = "\e[32m";
    private const string Reset = "\e[0m";

    /// <summary>Extracts the green (filled) portion between the ANSI markers.</summary>
    private static string Filled(double level)
    {
        var meter = Orchestrator.LevelMeter(level);
        var match = Regex.Match(meter, $"{Regex.Escape(Green)}(.*?){Regex.Escape(Reset)}");
        Assert.True(match.Success, $"no green segment in: {Escape(meter)}");
        return match.Groups[1].Value;
    }

    [Fact]
    public void AtRest_MeterIsEmptyAndStillFullWidth()
    {
        var meter = Orchestrator.LevelMeter(1e-9);

        Assert.Equal("", Filled(1e-9));
        // The raw meter ends with an ANSI reset; assert on the stripped
        // form: brackets + 8 ramp glyphs, regardless of fill.
        var plain = meter.Replace(Green, "").Replace(Reset, "");
        Assert.StartsWith("[", plain);
        Assert.EndsWith("]", plain);
        Assert.Equal(10, plain.Length);
    }

    [Fact]
    public void FullScale_FillsAllEightCells()
    {
        Assert.Equal("▁▂▃▄▅▆▇█", Filled(1.0));
    }

    [Fact]
    public void MidLevel_FillsProportionally()
    {
        // -40 dB → t = 0.2 → round(1.6) = 2 filled cells.
        Assert.Equal("▁▂", Filled(0.01));
    }

    [Fact]
    public void QuietButAudible_FillsOneCell()
    {
        // -48 dB → t = 0.04 → round(0.32) = 0 ... just below the -45 dB deadband? no deadband anymore; -48→0.04→0 cells.
        // Use -44 dB → t = 0.12 → round(0.96) = 1 cell.
        var level = Math.Pow(10, -44 / 20.0);
        Assert.Equal("▁", Filled(level));
    }

    private static string Escape(string s) => s.Replace("\e", "\\e");
}
