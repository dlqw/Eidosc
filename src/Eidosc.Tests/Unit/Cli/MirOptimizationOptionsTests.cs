using Eidosc.Cli.Commands;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Cli;

public class MirOptimizationOptionsTests
{
    [Fact]
    public void CompilationOptions_Default_EnablesMirOptimizations()
    {
        var options = new CompilationOptions();

        Assert.True(options.EnableMirOptimizations);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsEnabled_MapsNoMirOptFlagToOptimizationState(bool noMirOpt, bool expected)
    {
        Assert.Equal(expected, MirOptimizationOptions.IsEnabled(noMirOpt));
    }

    [Fact]
    public void CreateDisableOption_UsesNoMirOptAlias()
    {
        var option = MirOptimizationOptions.CreateDisableOption();

        Assert.Contains("--no-mir-opt", option.Aliases);
    }
}
