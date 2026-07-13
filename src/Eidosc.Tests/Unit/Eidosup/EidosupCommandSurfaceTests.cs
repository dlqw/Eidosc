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

    [Theory]
    [InlineData("toolchain link local ./build --dry-run")]
    [InlineData("toolchain unlink local --dry-run")]
    [InlineData("override set preview --path ./project --dry-run")]
    [InlineData("override unset --nonexistent")]
    [InlineData("override list --json")]
    [InlineData("set default-host linux-arm64 --dry-run")]
    [InlineData("set auto-self-update check-only --dry-run")]
    [InlineData("set auto-install disable --dry-run")]
    [InlineData("set source corp --dry-run")]
    [InlineData("source add corp index:https://dist.example.test/index.json --priority 200 --dry-run")]
    [InlineData("source remove corp --dry-run")]
    [InlineData("source list")]
    [InlineData("self update --check-only")]
    [InlineData("self uninstall --yes --keep-toolchains")]
    [InlineData("cache clean --max-size 2GiB --dry-run")]
    [InlineData("cache import ./bundle.zip")]
    [InlineData("cache export 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef ./bundle.zip")]
    [InlineData("completions powershell")]
    [InlineData("show active-toolchain --quiet --color never")]
    public void CreateParser_AcceptsDocumentedWp2Commands(string commandLine)
    {
        var result = global::Eidosup.Program.CreateParser().Parse(Split(commandLine));

        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("component list --installed --toolchain preview --json")]
    [InlineData("component add eidos-docs --toolchain preview --dry-run")]
    [InlineData("component remove eidos-docs --toolchain preview --dry-run")]
    [InlineData("target list --installed --toolchain preview --json")]
    [InlineData("target add linux-arm64 --toolchain preview --dry-run")]
    [InlineData("target remove linux-arm64 --toolchain preview --dry-run")]
    [InlineData("set profile complete --toolchain preview --dry-run")]
    [InlineData("doc compiler --toolchain preview --path")]
    public void CreateParser_AcceptsDocumentedWp3Commands(string commandLine)
    {
        var result = global::Eidosup.Program.CreateParser().Parse(Split(commandLine));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CreateParser_RejectsUnknownColorMode()
    {
        var result = global::Eidosup.Program.CreateParser().Parse(["show", "--color", "sometimes"]);

        Assert.NotEmpty(result.Errors);
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
