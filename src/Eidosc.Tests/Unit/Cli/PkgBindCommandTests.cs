using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Eidosc.Cli.Commands.Pkg;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class PkgBindCommandTests
{
    [Fact]
    public async Task PkgBindInit_ThenGenerateCheck_SucceedsWhenGeneratedFilesAreCurrent()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_pkg_bind");
        var tempDir = workspace.Root;
        var output = Console.Out;
        var error = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            var header = Path.Combine(tempDir, "demo.h");
            File.WriteAllText(header, "void demo_init(void);");
            var packageDir = Path.Combine(tempDir, "binding");
            var parser = new CommandLineBuilder(PkgBindCommand.Create()).Build();

            var initExitCode = await parser.InvokeAsync([
                "init",
                packageDir,
                "--package",
                "dev.eidos.demo",
                "--library",
                "demo",
                "--header",
                header
            ]);
            Assert.Equal(0, initExitCode);
            Assert.True(File.Exists(Path.Combine(packageDir, "bindgen.toml")));

            var generateExitCode = await parser.InvokeAsync(["generate", packageDir]);
            Assert.Equal(0, generateExitCode);

            var checkExitCode = await parser.InvokeAsync(["generate", packageDir, "--check"]);
            Assert.Equal(0, checkExitCode);
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
