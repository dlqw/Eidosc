using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Types;

public sealed class EffectPolymorphismTests
{
    private const string Prelude = """
io :: effect;
Alloc :: effect;

apply[A, B, E: effects] :: (A -> B need E) -> A -> B need E
{
    callback => value => callback(value)
}

write :: Int -> Int need io
{
    value => value
}
""";

    [Fact]
    public void Syntax_EffectRowParameter_ParsesSuccessfully()
    {
        var result = Run(Prelude, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var apply = Assert.Single(result.Ast!.Declarations.OfType<FuncDef>(), function => function.Name == "apply");
        Assert.Contains(apply.TypeParams, parameter => parameter.Name == "E" && parameter.IsEffectSet);
    }

    [Fact]
    public void Infer_EffectRowParameter_ProducesOpenRow()
    {
        var result = Run(Prelude, CompilationPhase.Types);
        Assert.True(result.Success, FormatDiagnostics(result));

        var inferer = new EffectInferer(result.SymbolTable!);
        inferer.Infer(result.Ast!);

        var apply = Assert.Single(result.Ast!.Declarations.OfType<FuncDef>(), function => function.Name == "apply");
        Assert.True(inferer.FunctionSummaries.TryGetValue(apply, out var summary));
        var effects = summary.InferredEffects;
        Assert.True(effects.IsPolymorphic, $"Expected open effect row, got {effects}");
        Assert.Single(effects.Variables);
    }

    [Fact]
    public void Authorize_HigherOrderEffect_WithMatchingCallerNeed_Succeeds()
    {
        const string caller = """

caller :: Int -> Int need io
{
    value => apply(write, value)
}
""";

        var result = Run(Prelude + caller, CompilationPhase.Effects);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Authorize_HigherOrderEffect_WithoutCallerNeed_ReportsMissingEffect()
    {
        const string caller = """

caller :: Int -> Int
{
    value => apply(write, value)
}
""";

        var result = Run(Prelude + caller, CompilationPhase.Effects);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E3003");
    }

    [Fact]
    public void Infer_IndependentEffectRows_RemainDistinct()
    {
        const string source = """
io :: effect;
Alloc :: effect;

compose[A, B, C, E: effects, F: effects] :: (B -> C need F) -> (A -> B need E) -> A -> C need E, F
{
    after => before => value => after(before(value))
}
""";

        var result = Run(source, CompilationPhase.Types);
        Assert.True(result.Success, FormatDiagnostics(result));

        var inferer = new EffectInferer(result.SymbolTable!);
        inferer.Infer(result.Ast!);

        var compose = Assert.Single(result.Ast!.Declarations.OfType<FuncDef>());
        Assert.True(inferer.FunctionSummaries.TryGetValue(compose, out var summary));
        var effects = summary.InferredEffects;
        Assert.Equal(2, effects.Variables.Count);
    }

    [Fact]
    public void Infer_DeclaredUpperBound_RemainsSeparateFromPureImplementation()
    {
        const string source = """
io :: effect;

declared_but_pure :: Int -> Int need io
{
    value => value
}
""";

        var result = Run(source, CompilationPhase.Types);
        Assert.True(result.Success, FormatDiagnostics(result));

        var inferer = new EffectInferer(result.SymbolTable!);
        inferer.Infer(result.Ast!);

        var function = Assert.Single(result.Ast!.Declarations.OfType<FuncDef>());
        Assert.True(inferer.FunctionSummaries.TryGetValue(function, out var summary));
        Assert.True(summary.DeclaredUpperBound.ContainsName("io"));
        Assert.True(summary.InferredEffects.IsPure);
    }

    [Fact]
    public void Infer_RecursiveCallGraph_PropagatesActualEffectsAcrossCycle()
    {
        const string source = """
io :: effect;

write :: Int -> Int need io
{
    value => value
}

first :: Int -> Int
{
    value => second(value)
}

second :: Int -> Int
{
    value => {
        written := write(value);
        first(written)
    }
}
""";

        var result = Run(source, CompilationPhase.Types);
        Assert.True(result.Success, FormatDiagnostics(result));

        var inferer = new EffectInferer(result.SymbolTable!);
        inferer.Infer(result.Ast!);

        var functions = result.Ast!.Declarations.OfType<FuncDef>().ToDictionary(static function => function.Name);
        Assert.True(inferer.FunctionSummaries[functions["first"]].InferredEffects.ContainsName("io"));
        Assert.True(inferer.FunctionSummaries[functions["second"]].InferredEffects.ContainsName("io"));
    }

    [Fact]
    public void Authorize_ReturnedEffectfulFunction_RequiresEffectOnlyWhenInvoked()
    {
        const string source = """
io :: effect;

write :: Int -> Int need io
{
    value => value
}

make :: Unit -> (Int -> Int need io)
{
    _ => write
}

caller :: Int -> Int
{
    value => make(())(value)
}
""";

        var named = Run(source, CompilationPhase.Namer);
        Assert.True(named.Success, FormatDiagnostics(named));
        var namedFunctions = named.Ast!.Declarations.OfType<FuncDef>().ToDictionary(static function => function.Name);
        var makeSignature = Assert.IsType<ArrowType>(Assert.Single(namedFunctions["make"].Signature));
        var returnedSignature = Assert.IsType<ArrowType>(makeSignature.ReturnType);
        var returnedEffect = Assert.Single(returnedSignature.RequiredEffects);
        var writeEffect = Assert.Single(namedFunctions["write"].RequiredAbilities);
        Assert.True(returnedEffect.SymbolId.IsValid);
        Assert.Equal(writeEffect.SymbolId, returnedEffect.SymbolId);

        var result = Run(source, CompilationPhase.Effects);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E3003");
    }

    [Fact]
    public void Authorize_TraitMethodEffectRow_InstantiatesCallbackEffect()
    {
        const string source = """
io :: effect;

Runner :: trait
{
    run[A, B, E: effects] :: Self -> (A -> B need E) -> A -> B need E
}

Box :: type
{
    Box :: type {}
}


run[A, B, E: effects] :: Box -> (A -> B need E) -> A -> B need E
 impl Runner
{
    _ => callback => value => callback(value)
}

write :: Int -> Int need io
{
    value => value
}

caller :: Int -> Int
{
    value => run(Box(), write, value)
}
""";

        var result = Run(source, CompilationPhase.Effects);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E3003");
    }

    private static CompilationResult Run(string source, CompilationPhase phase) =>
        new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "effect_polymorphism.eidos",
            StopAtPhase = phase,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
