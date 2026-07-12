using System.CommandLine.Parsing;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class EidosupCommandSurfaceTests
{
    [Theory]
    [InlineData("toolchain install preview")]
    [InlineData("toolchain list --json")]
    [InlineData("toolchain uninstall 0.4.0-alpha.2 --dry-run")]
    [InlineData("default preview")]
    [InlineData("default none --dry-run")]
    [InlineData("update preview --dry-run")]
    [InlineData("check preview --json")]
    [InlineData("show active-toolchain --json")]
    [InlineData("show home")]
    [InlineData("show profile")]
    [InlineData("which eidosc --toolchain preview")]
    [InlineData("rollback preview --dry-run")]
    public void CreateParser_AcceptsDocumentedWp1Commands(string commandLine)
    {
        var result = global::Eidosup.Program.CreateParser().Parse(Split(commandLine));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CreateParser_RunPreservesCommandArgumentsAfterDelimiter()
    {
        var result = global::Eidosup.Program.CreateParser().Parse(
            ["run", "preview", "--", "eidosc", "build", "project with spaces"]);

        Assert.Empty(result.Errors);
        Assert.Equal("run", result.CommandResult.Command.Name);
        Assert.Contains(result.Tokens, token => token.Value == "project with spaces");
    }

    [Theory]
    [InlineData("toolchain install")]
    [InlineData("toolchain uninstall")]
    [InlineData("run preview")]
    [InlineData("which")]
    public void CreateParser_RejectsMissingRequiredArguments(string commandLine)
    {
        var result = global::Eidosup.Program.CreateParser().Parse(Split(commandLine));

        Assert.NotEmpty(result.Errors);
    }

    private static IReadOnlyList<string> Split(string value) =>
        CommandLineStringSplitter.Instance.Split(value).ToArray();
}
