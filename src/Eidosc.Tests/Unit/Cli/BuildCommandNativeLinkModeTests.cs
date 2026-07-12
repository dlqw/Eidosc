using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Eidosc.Cli.Commands;
using Eidosc.CodeGen;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandNativeLinkModeTests
{
    [Theory]
    [InlineData("platform-default", NativeLinkMode.PlatformDefault)]
    [InlineData("no-pie", NativeLinkMode.NonPieExecutable)]
    [InlineData("pie", NativeLinkMode.PieExecutable)]
    public void ParseNativeLinkModeOption_MapsCliValuesToBackendModes(
        string value,
        NativeLinkMode expected)
    {
        Assert.Equal(expected, BuildCommand.ParseNativeLinkModeOption(value));
    }

    [Fact]
    public async Task BuildCommand_InvalidNativeLinkModeInvocation_ReturnsFailureInsteadOfThrowing()
    {
        var parser = new CommandLineBuilder(BuildCommand.Create()).Build();

        var exitCode = await parser.InvokeAsync(["main.eidos", "--native-link-mode", "static"]);

        Assert.NotEqual(0, exitCode);
    }
}
