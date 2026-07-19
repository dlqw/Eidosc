using Eidosc.Cli.Commands;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class MetaCommandTests
{
    [Fact]
    public async Task Expand_RejectsRemovedLegacyTransformationSurface()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_meta_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "main.eidos");
        var generatedDir = Path.Combine(tempDir, "generated");
        await File.WriteAllTextAsync(sourcePath, """
deriveAnswer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        meta.warning(meta.span_of(input), "trace warning");
        meta.add_after(input, [meta.function("answer", [], Int, meta.expr_int(42))])
    }
}


Subject :: type  expand deriveAnswer
{
    value:: Int
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
            var exitCode = await MetaCommand.Create().InvokeAsync([
                "expand",
                sourcePath,
                "--format",
                "json",
                "--trace-comptime",
                "--comptime-budget",
                "200000",
                "--emit-generated",
                generatedDir,
                "--no-color"
            ]);

            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Contains("Target", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Transformation", stderr.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(stdout.ToString());

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Expand_RejectsNonPositiveComptimeBudget()
    {
        var originalError = Console.Error;
        var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            var exitCode = await MetaCommand.Create().InvokeAsync([
                "expand",
                "missing.eidos",
                "--comptime-budget",
                "0"
            ]);
            Assert.Equal(2, exitCode);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("--comptime-budget", stderr.ToString(), StringComparison.Ordinal);
    }
}
