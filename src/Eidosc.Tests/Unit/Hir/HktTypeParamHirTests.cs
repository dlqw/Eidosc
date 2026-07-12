using System.Linq;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Hir;

public class HktTypeParamHirTests
{
    [Fact]
    public void HirBuilder_ComptimeTypeParam_PreservesPhaseMarker()
    {
        const string source = """
typeId[comptime T: Type] :: T -> T
{
    value => value
}
""";

        var result = RunHir(source, "hir_comptime_type_param.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(module.Declarations.OfType<HirFunc>(), declaration => declaration.Name == "typeId");
        var typeParam = Assert.Single(function.TypeParams);
        Assert.True(typeParam.IsComptime);
        Assert.Equal("Type", typeParam.ComptimeTypeAnnotation);
        Assert.Contains("comptime=Type", HirFormatter.FormatHir(module), StringComparison.Ordinal);
    }

    [Fact]
    public void HirBuilder_FunctionTypeParams_PreserveInferredKinds()
    {
        const string source = """
ho[F, G: kind2] :: F[G] -> F[G]
{
    x => x
}
""";

        var result = RunHir(source, "hir_hkt_function_type_params.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(module.Declarations.OfType<HirFunc>(), declaration => declaration.Name == "ho");

        Assert.Equal(2, function.TypeParams.Count);
        Assert.Equal("F", function.TypeParams[0].Name);
        Assert.Equal("kind2 -> kind1", function.TypeParams[0].KindAnnotation);
        Assert.Equal("G", function.TypeParams[1].Name);
        Assert.Equal("kind2", function.TypeParams[1].KindAnnotation);
    }

    [Fact]
    public void HirBuilder_AdtTypeParamsAndEffectMarker_PreserveTheirShapes()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

UseK[K] :: type {
    UseK(K[Box])
}

HK :: effect;
""";

        var result = RunHir(source, "hir_hkt_adt_ability_type_params.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<HirModule>(result.HirModule);

        var adt = Assert.Single(module.Declarations.OfType<HirAdt>(), declaration => declaration.Name == "UseK");
        var adtTypeParam = Assert.Single(adt.TypeParams);
        Assert.Equal("K", adtTypeParam.Name);
        Assert.Equal("kind2 -> kind1", adtTypeParam.KindAnnotation);

        Assert.Single(module.Declarations.OfType<HirEffect>(), declaration => declaration.Name == "HK");
    }

    [Fact]
    public void HirBuilder_TraitTypeParams_PreserveKindAnnotations()
    {
        const string source = """
Functor[F: kind2] :: trait {
    fmap :: F[Int] -> Self
}
""";

        var result = RunHir(source, "hir_hkt_trait_type_params.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<HirModule>(result.HirModule);
        var trait = Assert.Single(module.Declarations.OfType<HirTrait>(), declaration => declaration.Name == "Functor");
        var typeParam = Assert.Single(trait.TypeParams);
        Assert.Equal("F", typeParam.Name);
        Assert.Equal("kind2", typeParam.KindAnnotation);
    }

    [Fact]
    public void HirBuilder_TraitTypeParams_PreserveInferredKinds()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

HK[K] :: trait {
    run :: K[Box] -> Self
}
""";

        var result = RunHir(source, "hir_hkt_trait_type_params_inferred.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<HirModule>(result.HirModule);
        var trait = Assert.Single(module.Declarations.OfType<HirTrait>(), declaration => declaration.Name == "HK");
        var typeParam = Assert.Single(trait.TypeParams);
        Assert.Equal("K", typeParam.Name);
        Assert.Equal("kind2 -> kind1", typeParam.KindAnnotation);
    }

    [Fact]
    public void HirBuilder_FunctionTypeParamConstraints_PreserveTraitTypeArgs()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

Functor[F: kind2] :: trait {
    fmap :: F[Int] -> Self
}

lift[T: Functor[Box]] :: T -> T
{
    value => value
}
""";

        var result = RunHir(source, "hir_hkt_trait_constraint_args.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(module.Declarations.OfType<HirFunc>(), declaration => declaration.Name == "lift");
        var typeParam = Assert.Single(function.TypeParams);
        var constraint = Assert.Single(typeParam.Constraints);

        Assert.Equal("Functor", constraint.Name);
        Assert.True(constraint.SymbolId.IsValid);
        Assert.Empty(constraint.ModulePath);

        var typeArg = Assert.Single(constraint.TypeArgs);
        Assert.Equal("Box", typeArg.DisplayText);
        Assert.True(typeArg.TypeId.IsValid);
    }

    private static CompilationResult RunHir(string source, string inputFile)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();
    }
}
