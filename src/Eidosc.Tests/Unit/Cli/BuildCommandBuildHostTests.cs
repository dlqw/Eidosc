using System.CommandLine;
using System.Text.Json;
using Eidosc.Cli.Commands;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class BuildCommandBuildHostTests
{
    [Fact]
    public async Task Build_EmitBuildGraphAndTraceBuildExposeTheBuildHostCommandSurface()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_cli");
        var graphPath = workspace.Path("artifacts", "build-graph.json");
        workspace.WriteText(
            "eidos.toml",
            """
            manifestSchema = 3

            [language]
            version = "0.5.0-alpha.1"

            [package]
            name = "dev.eidos.test.build-host-cli"
            version = "0.1.0"

            [build]
            program = "build.eidos"
            outputRoots = ["build/generated"]
            """);
        workspace.WriteText(
            "build.eidos",
            """
            Context :: comptime Build::context();
            Emit :: comptime Build::emit(Context);
            BuildGraph :: comptime Build::graph(Emit, [], []);
            """);
        workspace.WriteText(
            "src/Main.eidos",
            """
            main :: Unit -> Int
            {
                _ => 0
            }
            """);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = await BuildCommand.Create().InvokeAsync([
                "--project",
                workspace.Root,
                "--target",
                "Typed",
                "--no-cache",
                "--emit-build-graph",
                graphPath,
                "--trace-build",
                "--no-color"
            ]);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.True(File.Exists(graphPath));
        using var graph = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        Assert.Equal(1, graph.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(graph.RootElement.GetProperty("steps").EnumerateArray());
        var trace = stdout.ToString() + stderr.ToString();
        Assert.Contains("Build host", trace, StringComparison.Ordinal);
        Assert.Contains("BuildGraph", trace, StringComparison.Ordinal);
    }
}
