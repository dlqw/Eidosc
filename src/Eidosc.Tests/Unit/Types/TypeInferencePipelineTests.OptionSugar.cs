using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_OptionSuffix_DesugarsToStdOption()
    {
        const string source = """
import std.Option

fallback :: Int? -> Int
{
    value => value ?? 42
}
""";

        var result = RunPipeline(source, CompilationPhase.Types, UseOptionSugarFixturePath);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "fallback");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var paramType = Assert.IsType<TyCon>(Assert.Single(inferredType.Params));
        Assert.Equal("Option", paramType.Name);
        var innerType = Assert.IsType<TyCon>(Assert.Single(paramType.Args));
        Assert.Equal("Int", innerType.Name);
        var resultType = Assert.IsType<TyCon>(inferredType.Result);
        Assert.Equal("Int", resultType.Name);
    }

    [Fact]
    public void Types_Coalesce_RequiresOptionLeftOperand()
    {
        const string source = """
bad :: Int -> Int
{
    value => value ?? 42
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot unify type 'Int' with 'Option<Int>'", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_Coalesce_DesugarsToStdOptionUnwrapOrInHir()
    {
        const string source = """
import std.Option

fallback :: Int? -> Int
{
    value => value ?? 42
}
""";

        var result = RunPipeline(source, CompilationPhase.Hir, UseOptionSugarFixturePath);

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);

        var function = Assert.Single(result.HirModule!.Declarations.OfType<HirFunc>(), item => item.Name == "fallback");
        var unwrapCall = Assert.IsType<HirCall>(function.Body);
        var unwrapFunction = Assert.IsType<HirVar>(unwrapCall.Function);
        Assert.Equal(2, unwrapCall.Arguments.Count);
        var optionValue = Assert.IsType<HirVar>(unwrapCall.Arguments[0]);
        var fallbackValue = Assert.IsType<HirLiteral>(unwrapCall.Arguments[1]);

        Assert.Equal(HirCallSurfaceSyntax.OperatorDesugaring, unwrapCall.SurfaceSyntax);
        Assert.Equal("std.Option.unwrap_or", unwrapFunction.Name);
        Assert.Equal("value", optionValue.Name);
        Assert.Equal(42, fallbackValue.Value);
    }

    private static void UseOptionSugarFixturePath(CompilationOptions options)
    {
        options.InputFile = TestSourceLoader.GetFullPath("projects/test/src/types/option_suffix_coalesce.eidos");
    }
}

