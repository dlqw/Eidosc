using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Types;

public sealed class EffectInfererTests
{
    [Fact]
    public void Infer_PureFunction_HasEmptyInferredRow()
    {
        const string source = """
identity :: Int -> Int
{
    value => value
}
""";

        var effects = InferFunctionEffects(source, "identity");

        Assert.True(effects.IsPure);
    }

    [Fact]
    public void Infer_DeclaredNeed_RemainsSeparateFromPureImplementation()
    {
        const string source = """
io :: effect;

write :: Int -> Int need io
{
    value => value
}
""";

        var result = RunPipeline(source, "effect_inferer_declared.eidos");
        var inferer = new EffectInferer(result.SymbolTable!);
        inferer.Infer(result.Ast!);
        var function = Assert.Single(result.Ast!.Declarations.OfType<FuncDef>());
        var summary = inferer.FunctionSummaries[function];

        Assert.True(summary.DeclaredUpperBound.ContainsName("io"));
        Assert.True(summary.InferredEffects.IsPure);
    }

    [Fact]
    public void Infer_FunctionCallToPureImplementation_KeepsActualSummaryPure()
    {
        const string source = """
io :: effect;

write :: Int -> Int need io
{
    value => value
}

caller :: Int -> Int
{
    value => write(value)
}
""";

        var effects = InferFunctionEffects(source, "caller");

        Assert.True(effects.IsPure, $"Expected pure implementation summary, got {effects}");
    }

    [Fact]
    public void Infer_ImportedPureImplementation_KeepsActualSummaryPure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_effect_inferer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var moduleFile = Path.Combine(tempDir, "Cap.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        File.WriteAllText(moduleFile, """
Cap :: module {
    io :: effect;

    write :: Int -> Int need io
    {
        value => value
    }
}
""");
        File.WriteAllText(entryFile, """
import Cap

main :: Int -> Int
{
    value => Cap.write(value)
}
""");

        try
        {
            var result = RunPipeline(File.ReadAllText(entryFile), entryFile);
            var inferer = new EffectInferer(result.SymbolTable!);
            inferer.Infer(result.Ast!);
            var main = Assert.Single(result.Ast!.Declarations.OfType<FuncDef>());

            Assert.True(
                inferer.FunctionSummaries[main].InferredEffects.IsPure,
                $"Expected pure implementation summary, got {inferer.FunctionSummaries[main].InferredEffects}");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static EffectRow InferFunctionEffects(string source, string functionName)
    {
        var result = RunPipeline(source, "effect_inferer_tests.eidos");
        var inferer = new EffectInferer(result.SymbolTable!);
        inferer.Infer(result.Ast!);
        var function = Assert.Single(
            result.Ast!.Declarations.OfType<FuncDef>(),
            candidate => candidate.Name == functionName);
        return inferer.FunctionSummaries[function].InferredEffects;
    }

    private static CompilationResult RunPipeline(string source, string inputFile)
    {
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = inputFile,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.NotNull(result.Ast);
        Assert.NotNull(result.SymbolTable);
        return result;
    }
}
