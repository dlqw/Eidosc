using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Types;

public sealed class EffectTagLanguageContractTests
{
    [Fact]
    public void MarkerEffect_ParsesAsDedicatedDeclaration()
    {
        const string source = """
IO :: effect;
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var declaration = Assert.Single(module.Declarations);
        Assert.Equal("EffectDef", declaration.GetType().Name);
    }

    [Fact]
    public void MarkerEffect_ResolvesAndCanAppearInNeedClause()
    {
        const string source = """
IO :: effect;

write :: String -> Unit need io
{
    _ => ()
}
""";

        var result = Run(source, CompilationPhase.Effects);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void MarkerEffect_TypeParametersAreRejected()
    {
        const string source = """
State[S] :: effect;
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void MarkerEffect_OperationMembersAreRejected()
    {
        const string source = """
IO :: effect
{
    write :: String -> Unit
}
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void LegacyEffectDeclaration_IsRejectedWithoutCompatibilityFallback()
    {
        const string source = """
IO :: ability {
    write :: String -> Unit
}
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void HandlerExpression_IsRejectedWithoutCompatibilityFallback()
    {
        const string source = """
IO :: effect;

main :: Unit -> Unit
{
    _ => handler IO { }
}
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void WithClause_IsRejectedWithoutCompatibilityFallback()
    {
        const string source = """
IO :: effect;

write :: Unit -> Unit need io
{
    _ => ()
}

main :: Unit -> Unit
{
    _ => write(()) with ignored
}
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void NeedBound_MissingCallerEffectIsRejected()
    {
        const string source = """
IO :: effect;

write :: Unit -> Unit need io
{
    _ => ()
}

caller :: Unit -> Unit
{
    _ => write(())
}
""";

        var result = Run(source, CompilationPhase.Effects);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
    }

    [Fact]
    public void NeedBound_DeclaredCallerEffectAuthorizesCall()
    {
        const string source = """
IO :: effect;

write :: Unit -> Unit need io
{
    _ => ()
}

caller :: Unit -> Unit need io
{
    _ => write(())
}
""";

        var result = Run(source, CompilationPhase.Effects);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void HigherOrderEffectRow_PropagatesCallbackEffects()
    {
        const string source = """
IO :: effect;

apply[A, B, E: effects] :: (A -> B need E) -> A -> B need E
{
    callback => value => callback(value)
}

write_number :: Int -> Int need io
{
    value => value
}

caller :: Int -> Int need io
{
    value => apply(write_number, value)
}
""";

        var result = Run(source, CompilationPhase.Effects);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    private static CompilationResult Run(string source, CompilationPhase stopAtPhase)
    {
        var options = new CompilationOptions
        {
            InputFile = "effect_tag_language_contract.eidos",
            StopAtPhase = stopAtPhase,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
