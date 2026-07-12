using Eidosc.Symbols;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class TypeParamKindSymbolTests
{
    [Fact]
    public void CompilationPipeline_ComptimeTypeParamSymbol_PreservesTypeLevelComptimeMarker()
    {
        const string source = """
typeId[comptime T: Type] :: T -> T
{
    value => value
}
""";

        var result = RunNamer(source, "comptime_type_param_symbol_tests.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "typeId");
        var typeParam = Assert.Single(function.TypeParams);
        Assert.True(typeParam.IsComptime);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.True(symbol.IsComptime);
        Assert.Equal("Type", symbol.ComptimeTypeAnnotation);
        Assert.Equal("kind1", symbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_ComptimeConstGenericParam_ReportsUnsupportedValueLevelGeneric()
    {
        const string source = """
use[comptime N: Int] :: Unit -> Unit
{
    _ => ()
}
""";

        var result = RunNamer(source, "comptime_const_generic_param_tests.eidos");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("value-level const generics are not implemented yet", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TypeParamSymbol_PreservesParenthesizedKindAnnotation()
    {
        const string source = """
ho[F: kind2 -> kind1, G: kind2] :: F[G] -> F[G]
{
    x => x
}
""";

        var result = RunNamer(source, "type_param_kind_symbol_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "ho");
        var typeParam = function.TypeParams[0];
        Assert.True(typeParam.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.Equal("kind2 -> kind1", symbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_AdtSymbol_TypeParams_PopulatedWithResolvedTypeParamSymbols()
    {
        const string source = """
Lift[F: kind2] :: type {
    Lift(F[Int])
}
""";

        var result = RunNamer(source, "adt_type_param_symbol_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var adt = Assert.Single(module.Declarations.OfType<AdtDef>(), declaration => declaration.Name == "Lift");
        Assert.True(adt.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var adtSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(adt.SymbolId));
        var typeParamSymbolId = Assert.Single(adtSymbol.TypeParams);
        Assert.True(typeParamSymbolId.IsValid);
        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParamSymbolId));
        Assert.Equal("kind2", typeParamSymbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_TraitSymbol_TypeParams_PopulatedWithResolvedTypeParamSymbols()
    {
        const string source = """
Functor[F: kind2] :: trait {
    fmap :: F -> Self
}
""";

        var result = RunNamer(source, "trait_type_param_symbol_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var trait = Assert.Single(module.Declarations.OfType<TraitDef>(), declaration => declaration.Name == "Functor");
        Assert.True(trait.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitSymbol = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(trait.SymbolId));
        var typeParamSymbolId = Assert.Single(traitSymbol.TypeParams);
        Assert.True(typeParamSymbolId.IsValid);
        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParamSymbolId));
        Assert.Equal("kind2", typeParamSymbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_TypesPhase_UnannotatedFunctionTypeParamKind_IsWrittenBackToSymbol()
    {
        const string source = """
ho[F, G: kind2] :: F[G] -> F[G]
{
    x => x
}
""";

        var result = RunPipeline(source, "function_type_param_inferred_kind_tests.eidos", CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "ho");
        var typeParam = function.TypeParams[0];
        Assert.True(typeParam.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.Equal("kind2 -> kind1", typeParamSymbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_TypesPhase_UnannotatedAdtTypeParamKind_IsWrittenBackToSymbol()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

UseK[K] :: type {
    UseK(K[Box])
}
""";

        var result = RunPipeline(source, "adt_type_param_inferred_kind_symbol_tests.eidos", CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var adt = Assert.Single(module.Declarations.OfType<AdtDef>(), declaration => declaration.Name == "UseK");
        var typeParam = Assert.Single(adt.TypeParams);
        Assert.True(typeParam.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.Equal("kind2 -> kind1", typeParamSymbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_TypesPhase_UnannotatedTraitTypeParamKind_IsWrittenBackToSymbol()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

HK[K] :: trait {
    run :: K[Box] -> Self
}
""";

        var result = RunPipeline(source, "trait_type_param_inferred_kind_symbol_tests.eidos", CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var trait = Assert.Single(module.Declarations.OfType<TraitDef>(), declaration => declaration.Name == "HK");
        var typeParam = Assert.Single(trait.TypeParams);
        Assert.True(typeParam.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.Equal("kind2 -> kind1", typeParamSymbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_TypesPhase_TraitMethod_UnannotatedMethodTypeParamKind_IsWrittenBackToSymbol()
    {
        const string source = """
Traversable[T: kind2] :: trait {
    traverse[A, B, G] :: (A -> G[B]) -> T[A] -> G[T[B]]
}
""";

        var result = RunPipeline(source, "trait_method_type_param_inferred_kind_symbol_tests.eidos", CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var trait = Assert.Single(module.Declarations.OfType<TraitDef>(), declaration => declaration.Name == "Traversable");
        var method = Assert.Single(trait.Methods, declaration => declaration.Name == "traverse");
        var methodTypeParam = Assert.Single(method.TypeParams, typeParam => typeParam.Name == "G");
        Assert.True(methodTypeParam.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(methodTypeParam.SymbolId));
        Assert.Equal("kind2", typeParamSymbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_TypesPhase_ParenthesizedHigherOrderKind_WorksInsideAdtUse()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

ApplyToInt[F: kind2] :: type {
    ApplyToInt(F[Int])
}

ho[F: kind2 -> kind1, G: kind2] :: F[G] -> F[G]
{
    x => x
}

use :: ApplyToInt[Box] -> ApplyToInt[Box]
{
    x => ho(x)
}
""";

        var result = RunPipeline(source, "parenthesized_higher_order_kind_adt_use_tests.eidos", CompilationPhase.Types);

        Assert.True(result.Success);
    }

    [Fact]
    public void CompilationPipeline_TypesPhase_UnannotatedAdtKinds_WorkAcrossCtorBindings()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

Lift[F] :: type {
    Lift(F[Int])
}

ApplyToInt[F: kind2] :: type {
    ApplyToInt(F[Int])
}

UseK[K] :: type {
    UseK(K[Box])
}

useLift :: Lift[Box] -> Lift[Box]
{
    x => x
}

useUseK :: UseK[ApplyToInt] -> UseK[ApplyToInt]
{
    x => x
}
""";

        var result = RunPipeline(source, "unannotated_adt_kind_ctor_binding_tests.eidos", CompilationPhase.Types);

        Assert.True(result.Success);
    }

    [Fact]
    public void CompilationPipeline_Namer_TypeParamTraitConstraintTypeArgs_AreResolved()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

Functor[F: kind2] :: trait {
    fmap :: F[Int] -> Self
}

use[T: Functor[Box]] :: T -> T
{
    x => x
}
""";

        var result = RunNamer(source, "trait_constraint_type_args_symbol_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var typeParam = Assert.Single(function.TypeParams);
        var traitConstraint = Assert.Single(typeParam.TraitConstraints);

        Assert.True(traitConstraint.SymbolId.IsValid);
        var traitTypeArg = Assert.Single(traitConstraint.TypeArgs);
        var traitTypeArgPath = Assert.IsType<Eidosc.Ast.Types.TypePath>(traitTypeArg);
        Assert.True(traitTypeArgPath.SymbolId.IsValid);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.Contains(traitConstraint.SymbolId, typeParamSymbol.TraitConstraints);
    }

    [Fact]
    public void CompilationPipeline_Namer_TypeParamConstraint_EffectIsAccepted()
    {
        const string source = """
Writer :: effect;

use[T: Writer] :: T -> T
{
    x => x
}
""";

        var result = RunNamer(source, "type_param_constraint_ability_accepted.eidos");

        // Effect rows are allowed as type-parameter constraints for effect polymorphism.
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.NotNull(result.Ast);

        // Verify the effect constraint is recorded on the type parameter.
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var typeParam = Assert.Single(function.TypeParams);
        var traitConstraint = Assert.Single(typeParam.TraitConstraints);

        Assert.True(traitConstraint.SymbolId.IsValid);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var resolvedSymbol = symbolTable.GetSymbol(traitConstraint.SymbolId);
        Assert.IsType<EffectSymbol>(resolvedSymbol);

        var typeParamSymbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.Contains(traitConstraint.SymbolId, typeParamSymbol.TraitConstraints);
    }

    private static CompilationResult RunNamer(string source, string inputFile)
    {
        return RunPipeline(source, inputFile, CompilationPhase.Namer);
    }

    private static CompilationResult RunPipeline(string source, string inputFile, CompilationPhase phase)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = phase,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
