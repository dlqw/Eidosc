using Eidosc.Pipeline;
using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Ide;
using Eidosc.Tests.Fixtures;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_PipeOperator_AppliesRightCallableToLeftValue()
    {
        const string source = """
inc :: Int -> Int
{
    x => x + 1
}

main :: Unit -> Int
{
    _ => 1 |> inc
}
""";

        var result = RunPipeline(source, CompilationPhase.Types, options => options.NoImplicitPrelude = true);

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "Expected success");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_PipeOperator_UsesPrecompiledCallableWithoutImport()
    {
        const string source = """
main :: Unit -> Option[Int]
{
    _ => [1, 2] |> head
}
""";

        var result = RunPipeline(
            source,
            CompilationPhase.Types,
            options => options.InputFile = TestSourceLoader.GetFullPath(
                TestPathConfig.Current.TutorialExample("29_precompiled_stdlib.eidos")));

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "Expected success");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_InfixCall_UsesPrecompiledCallableWithoutImport()
    {
        const string source = """
main :: Unit -> Float
{
    _ => 2.0 `pow` 3.0
}
""";

        var result = RunPipeline(
            source,
            CompilationPhase.Types,
            options => options.InputFile = TestSourceLoader.GetFullPath(
                TestPathConfig.Current.TutorialExample("29_precompiled_stdlib.eidos")));

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "Expected success");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_StdlibSymbolicOperators_InferWithoutUnsupportedDiagnostics()
    {
        const string source = """
import Std.Applicative
import Std.Fn
import Std.Functor
import Std.Monad
import Std.Option
import Std.Semigroup

inc :: Int -> Int
{
    x => x + 1
}

double :: Int -> Int
{
    x => x + x
}

lift_plus_ten :: Int -> Option[Int]
{
    x => Some(x + 10)
}

collapse :: Option[Int] -> Int
{
    Some(value) => value,
    None() => 0
}

main :: Unit -> Int
{
    _ => {
        piped := 4 |> inc;
        composedRight := (inc >>> double)(3);
        composedLeft := (double <<< inc)(3);
        appended := 20 <> 3;
        mapped := collapse(inc <$> Some(5));
        applied := collapse(Some(inc) <*> Some(5));
        bound := collapse(Some(5) >>= lift_plus_ten);
        piped + composedRight + composedLeft + appended + mapped + applied + bound
    }
}
""";

        var result = RunPipeline(
            source,
            CompilationPhase.Types,
            options => options.InputFile = TestSourceLoader.GetFullPath(
                TestPathConfig.Current.TutorialExample("29_precompiled_stdlib.eidos")));

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("not supported by type inference", StringComparison.Ordinal));

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var main = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "main");
        var mainType = Assert.IsType<TyFun>(main.InferredType);

        Assert.Equal("Int", Assert.IsType<TyCon>(mainType.Result).Name);
    }

    [Fact]
    public void Types_InvalidFmapOperand_ReportsMismatchWithoutTrustworthyFreshType()
    {
        const string source = """
import Std.Functor
import Std.Option

inc :: Int -> Int
{
    x => x + 1
}

main :: Unit -> Int
{
    _ => {
        bad := inc <$> 5;
        good := 1;
        good
    }
}
""";

        var result = RunPipeline(
            source,
            CompilationPhase.Types,
            options => options.InputFile = TestSourceLoader.GetFullPath(
                TestPathConfig.Current.TutorialExample("29_precompiled_stdlib.eidos")));

        Assert.False(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("not supported by type inference", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Fmap right operand", StringComparison.Ordinal));

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, symbol => symbol.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, symbol => symbol.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
    }
}

