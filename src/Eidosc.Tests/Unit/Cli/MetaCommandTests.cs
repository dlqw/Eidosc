using Eidosc.Cli.Commands;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class MetaCommandTests
{
    [Fact]
    public async Task Expand_JsonTraceAndGeneratedDocuments_AreEmittedWithoutMixingStreams()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_meta_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "main.eidos");
        var generatedDir = Path.Combine(tempDir, "generated");
        await File.WriteAllTextAsync(sourcePath, """
deriveAnswer :: comptime Meta.DeriveInput -> Meta.Expansion {
    input => {
        Meta.warning(Meta.deriveSpan(input), "trace warning");
        Meta.expansion([Meta.function("answer", [], Int, Meta.exprInt(42))])
    }
}

@derive(deriveAnswer)
Subject :: type {
    value: Int
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

        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("eidos-meta-expansion-v1", json.RootElement.GetProperty("SchemaVersion").GetString());
        var declaration = Assert.Single(json.RootElement.GetProperty("Declarations").EnumerateArray());
        Assert.Equal("answer", declaration.GetProperty("Name").GetString());
        Assert.Contains("[comptime #", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Meta.warning", stderr.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(generatedDir, "generated-manifest.json")));
        Assert.True(Directory.EnumerateFiles(generatedDir, "*.eidos").Any());

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
