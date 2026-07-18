using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Types;

public sealed class EffectCallShapeTests
{
    [Fact]
    public void Authorize_ZeroArgumentEffectfulCall_UsesInstantiatedCallEffects()
    {
        const string source = """
io :: effect;

tick :: Unit -> Int need io
{
    _ => 1
}

caller :: Unit -> Int
{
    _ => tick()
}
""";

        var result = Run(source);

        AssertMissingIo(result);
        var call = Assert.Single(EnumerateNodes(result).OfType<CallExpr>());
        AssertIo(call.InferredEffects);
    }

    [Fact]
    public void Authorize_OverloadSelectedEffectfulCall_UsesSelectedEffects()
    {
        const string source = """
io :: effect;

pick :: Int -> Int need io
{
    value => value
}

pick :: String -> Int
{
    _ => 0
}

caller :: Int -> Int
{
    value => pick(value)
}
""";

        var result = Run(source);

        AssertMissingIo(result);
        var call = Assert.Single(EnumerateNodes(result).OfType<CallExpr>());
        AssertIo(call.InferredEffects);
    }

    [Fact]
    public void Authorize_EffectfulMethodCall_UsesInstantiatedCallEffects()
    {
        const string source = """
io :: effect;

write :: Int -> Int need io
{
    value => value
}

caller :: Int -> Int
{
    value => value.write()
}
""";

        var result = Run(source);

        AssertMissingIo(result);
        var call = Assert.Single(EnumerateNodes(result).OfType<MethodCallExpr>());
        AssertIo(call.InferredEffects);
    }

    [Fact]
    public void Authorize_EffectfulInfixCall_UsesInstantiatedCallEffects()
    {
        const string source = """
io :: effect;

join :: Int -> Int -> Int need io
{
    left => right => left + right
}

caller :: Int -> Int
{
    value => value `join` 1
}
""";

        var result = Run(source);

        AssertMissingIo(result);
        var call = Assert.Single(EnumerateNodes(result).OfType<InfixCallExpr>());
        AssertIo(call.InferredEffects);
    }

    [Fact]
    public void Authorize_PartialApplication_DoesNotExecuteLatentEffects()
    {
        const string source = """
io :: effect;

join :: Int -> Int -> Int need io
{
    left => right => left + right
}

partial :: join(1);
""";

        var result = Run(source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var call = Assert.Single(EnumerateNodes(result).OfType<CallExpr>());
        Assert.Null(call.InferredEffects);
        var resultType = Assert.IsType<TyFun>(call.InferredType);
        Assert.True(resultType.Effects.ContainsName("io"));
    }

    private static CompilationResult Run(string source) =>
        new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "effect_call_shapes.eidos",
            StopAtPhase = CompilationPhase.Effects,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

    private static IEnumerable<Eidosc.Ast.EidosAstNode> EnumerateNodes(CompilationResult result) =>
        AstStableNodeTraversal.Enumerate(Assert.IsType<ModuleDecl>(result.Ast))
            .Select(static entry => entry.Node);

    private static void AssertMissingIo(CompilationResult result)
    {
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "E3003");
    }

    private static void AssertIo(EffectRow? effects)
    {
        Assert.NotNull(effects);
        Assert.True(effects.ContainsName("io"), $"Expected io effect, got {effects}");
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
