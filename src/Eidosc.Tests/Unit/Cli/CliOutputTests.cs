using Eidosc.Cli.Commands;

namespace Eidosc.Tests.Unit.Cli;

public class CliOutputTests
{
    [Fact]
    public void WriteAction_WritesCargoStyleActionLabel()
    {
        using var output = new StringWriter();

        CliOutput.WriteAction("Compiling", "src/Main.eidos (target LlvmIr)", useColors: false, output);

        Assert.Equal($"   Compiling src/Main.eidos (target LlvmIr){Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void WriteFinished_WithDetails_WritesElapsedResult()
    {
        using var output = new StringWriter();

        CliOutput.WriteFinished(
            "build",
            success: true,
            TimeSpan.FromMilliseconds(1234),
            useColors: false,
            details: "phase Llvm, target LlvmIr",
            output);

        Assert.Equal($"    Finished build in 1.23s (phase Llvm, target LlvmIr){Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void WriteArtifact_WritesArtifactKindAndPath()
    {
        using var output = new StringWriter();

        CliOutput.WriteArtifact("llvm-ir", "build/Main.ll", useColors: false, output);

        Assert.Equal($"    Artifact llvm-ir: build/Main.ll{Environment.NewLine}", output.ToString());
    }
}
