using Eidosup.Proxies;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ProxyProcessRunnerTests
{
    [Fact]
    public void CreateStartInfo_ForwardsArgumentsAndSetsSelectedToolchainEnvironment()
    {
        var resolved = CreateResolved("compiler");

        var startInfo = ProxyProcessRunner.CreateStartInfo(
            resolved,
            ["build", "path with spaces/source.eidos", "--phase", "hir"]);

        Assert.Equal(resolved.CommandPath, startInfo.FileName);
        Assert.Equal(["build", "path with spaces/source.eidos", "--phase", "hir"], startInfo.ArgumentList);
        Assert.Equal(resolved.RootDirectory, startInfo.Environment["EIDOS_HOME"]);
        Assert.Equal(resolved.ToolchainDirectory, startInfo.Environment["EIDOSC_HOME"]);
        Assert.Equal(resolved.RuntimePath, startInfo.Environment["EIDOS_RUNTIME_PATH"]);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
    }

    [Fact]
    public async Task RunAsync_ReturnsChildExitCodeWithoutTranslation()
    {
        var command = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", "exit 37" }
            : new[] { "-c", "exit 37" };
        var runner = new ProxyProcessRunner();

        var exitCode = await runner.RunAsync(
            CreateResolved(command),
            arguments,
            CancellationToken.None);

        Assert.Equal(37, exitCode);
    }

    private static ResolvedToolchain CreateResolved(string commandPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "eidos-proxy");
        var toolchain = Path.Combine(root, "toolchains", "selected");
        return new ResolvedToolchain(
            "preview",
            ToolchainSelectionSource.Default,
            "selected",
            toolchain,
            commandPath,
            Path.Combine(toolchain, "runtime"),
            root);
    }
}
