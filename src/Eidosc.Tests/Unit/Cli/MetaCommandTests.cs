using Eidosc.Cli.Commands;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class MetaCommandTests
{
    [Fact]
    public async Task Expand_UsesTypedDeriveProtocolAndEmitsGeneratedDocument()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_meta_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "main.eidos");
        var generatedDir = Path.Combine(tempDir, "generated");
        await File.WriteAllTextAsync(sourcePath, """
derive_answer :: comptime meta.Type -> meta.Items {
    _ => [meta.function("answer", [], Int, meta.expr_int(42))]
}

@[expand(derive_answer)]
Subject :: type {
    value :: Int
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

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.DoesNotContain("Target", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Transformation", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("answer", stdout.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFiles(generatedDir, "*", SearchOption.AllDirectories));
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
