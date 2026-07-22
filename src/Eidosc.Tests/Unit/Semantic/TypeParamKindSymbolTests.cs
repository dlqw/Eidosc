using Eidosc.Symbols;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
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
        Assert.Equal(GenericParameterKind.Type, typeParam.ParameterKind);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.True(symbol.IsComptime);
        Assert.Equal(GenericParameterKind.Type, symbol.ParameterKind);
        Assert.Equal("Type", symbol.ComptimeTypeAnnotation);
        Assert.Equal("kind1", symbol.KindAnnotation);
    }

    [Fact]
    public void CompilationPipeline_ComptimeConstGenericParam_UsesValueNamespaceAndPreservesKind()
    {
        const string source = """
use[comptime N: Int] :: Unit -> Int
{
    _ => N
}
""";

        var result = RunNamer(source, "comptime_const_generic_param_tests.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var typeParam = Assert.Single(function.TypeParams);
        Assert.Equal(GenericParameterKind.Value, typeParam.ParameterKind);

        var bodyReference = Assert.IsType<Eidosc.Ast.Expressions.IdentifierExpr>(Assert.Single(function.Body).Expression);
        Assert.Equal(typeParam.SymbolId, bodyReference.SymbolId);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.True(symbol.IsComptime);
        Assert.Equal(GenericParameterKind.Value, symbol.ParameterKind);
        Assert.Equal("Int", symbol.ComptimeTypeAnnotation);
    }

    [Fact]
    public void CompilationPipeline_EffectRowGenericParam_PreservesDistinctKind()
    {
        const string source = """
use[E: effects] :: Unit -> Unit
{
    _ => ()
}
""";

        var result = RunNamer(source, "effect_row_generic_param_tests.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var typeParam = Assert.Single(function.TypeParams);
        Assert.Equal(GenericParameterKind.EffectRow, typeParam.ParameterKind);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbol = Assert.IsType<TypeParamSymbol>(symbolTable.GetSymbol(typeParam.SymbolId));
        Assert.Equal(GenericParameterKind.EffectRow, symbol.ParameterKind);
    }

    [Fact]
    public void CompilationPipeline_GenericApplication_ResolvesOrderedValueAndTypeArgumentsByDeclarationDomain()
    {
        const string source = """
Size :: comptime 4;

Vector[comptime N: Int, comptime T: Type] :: type {
    Vector:: type(T)
}

use :: Vector[Size, Int] -> Unit
{
    _ => ()
}
""";

        var result = RunNamer(source, "resolved_generic_argument_domains.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var size = Assert.Single(module.Declarations.OfType<LetDecl>());
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var signature = Assert.IsType<Eidosc.Ast.Types.ArrowType>(Assert.Single(function.Signature));
        var vector = Assert.IsType<Eidosc.Ast.Types.TypePath>(signature.ParamType);

        Assert.Equal(2, vector.GenericArguments.Count);
        var valueArgument = Assert.IsType<Eidosc.Ast.Types.ValueGenericArgumentNode>(vector.GenericArguments[0]);
        var valuePath = Assert.IsType<Eidosc.Ast.Expressions.PathExpr>(valueArgument.Expression);
        Assert.Equal(size.SymbolId, valuePath.SymbolId);
        Assert.IsType<Eidosc.Ast.Types.TypeGenericArgumentNode>(vector.GenericArguments[1]);
        Assert.Single(vector.TypeArgs);
    }

    [Fact]
    public void CompilationPipeline_Types_ValueGenericArgumentParticipatesInNominalTypeIdentity()
    {
        const string source = """
Vector[comptime N: Int, comptime T: Type] :: type {
    Vector:: type(T)
}

use :: Vector[4, Int] -> Vector[4, Int]
{
    value => value
}
""";

        var result = RunPipeline(source, "value_generic_type_identity.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var functionType = Assert.IsType<TyFun>(function.InferredType);
        var parameterType = Assert.IsType<TyCon>(Assert.Single(functionType.Params));
        var resultType = Assert.IsType<TyCon>(functionType.Result);
        var parameterValue = Assert.Single(parameterType.ValueArgs);
        var resultValue = Assert.Single(resultType.ValueArgs);

        Assert.Equal(0, parameterValue.ParameterIndex);
        Assert.Equal(parameterValue.CanonicalHash, resultValue.CanonicalHash);
        Assert.Equal("4", parameterValue.DisplayText);
        Assert.True(parameterType == resultType || parameterType.ToString() == resultType.ToString());
    }

    [Fact]
    public void CompilationPipeline_Types_DifferentValueGenericArgumentsDoNotUnify()
    {
        const string source = """
Vector[comptime N: Int, comptime T: Type] :: type {
    Vector:: type(T)
}

bad :: Vector[4, Int] -> Vector[5, Int]
{
    value => value
}
""";

        var result = RunPipeline(source, "value_generic_type_mismatch.eidos", CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Vector", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("4", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("5", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_Types_OpenValueParameterCanFlowIntoNestedGenericType()
    {
        const string source = """
Buffer[comptime N: Int, comptime T: Type] :: type {
    Buffer:: type(T)
}

Wrapper[comptime N: Int, comptime T: Type] :: type {
    Wrapper:: type(Buffer[N, T])
}

use :: Wrapper[4, Int] -> Wrapper[4, Int]
{
    value => value
}
""";

        var result = RunPipeline(source, "nested_open_value_generic.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var wrapper = Assert.Single(module.Declarations.OfType<AdtDef>(), declaration => declaration.Name == "Wrapper");
        var constructor = Assert.Single(wrapper.Constructors);
        var buffer = Assert.IsType<Eidosc.Ast.Types.TypePath>(Assert.Single(constructor.PositionalArgs));
        var symbolicValue = Assert.IsType<Eidosc.Ast.Types.ValueGenericArgumentNode>(buffer.GenericArguments[0]);
        Assert.Equal(wrapper.TypeParams[0].SymbolId, symbolicValue.Expression.SymbolId);
    }

    [Fact]
    public void CompilationPipeline_Types_ExplicitConstructorValueArgumentSpecializesReturnType()
    {
        const string source = """
Vector[comptime N: Int, comptime T: Type] :: type {
    Vector:: type(T)
}

make :: Unit -> Vector[4, Int]
{
    _ => Vector[4, Int](1)
}
""";

        var result = RunPipeline(source, "explicit_constructor_value_generic.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "make");
        var constructor = Assert.IsType<Eidosc.Ast.Expressions.CallExpr>(Assert.Single(function.Body).Expression);
        Assert.NotNull(constructor.InferredType);
        var functionType = Assert.IsType<TyFun>(function.InferredType);
        var resultType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal("4", Assert.Single(resultType.ValueArgs).DisplayText);
    }

    [Fact]
    public void CompilationPipeline_Types_ExpectedTypeRefinesImplicitConstructorValueArgument()
    {
        const string source = """
Vector[comptime N: Int, comptime T: Type] :: type {
    Vector:: type(T)
}

make :: Unit -> Vector[4, Int]
{
    _ => Vector(1)
}
""";

        var result = RunPipeline(source, "expected_constructor_value_generic.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "make");
        var constructor = Assert.IsType<Eidosc.Ast.Expressions.CallExpr>(Assert.Single(function.Body).Expression);
        Assert.NotNull(constructor.InferredType);
        var functionType = Assert.IsType<TyFun>(function.InferredType);
        var resultType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal("4", Assert.Single(resultType.ValueArgs).DisplayText);
    }

    [Fact]
    public void CompilationPipeline_Types_NestedConstructorSharesValueBindingWithReturnType()
    {
        const string source = """
Buffer[comptime N: Int, comptime T: Type] :: type {
    Buffer:: type(T)
}

Wrapper[comptime N: Int, comptime T: Type] :: type {
    Wrapper:: type(Buffer[N, T])
}

make :: Buffer[4, Int] -> Wrapper[4, Int]
{
    value => Wrapper(value)
}
""";

        var result = RunPipeline(source, "nested_constructor_value_binding.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "make");
        var constructor = Assert.IsType<Eidosc.Ast.Expressions.CallExpr>(Assert.Single(function.Body).Expression);
        Assert.NotNull(constructor.InferredType);
        var functionType = Assert.IsType<TyFun>(function.InferredType);
        var resultType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal("4", Assert.Single(resultType.ValueArgs).DisplayText);
    }

    [Fact]
    public void CompilationPipeline_Types_NestedConstructorRejectsConflictingValueBinding()
    {
        const string source = """
Buffer[comptime N: Int, comptime T: Type] :: type {
    Buffer:: type(T)
}

Wrapper[comptime N: Int, comptime T: Type] :: type {
    Wrapper:: type(Buffer[N, T])
}

bad :: Buffer[5, Int] -> Wrapper[4, Int]
{
    value => Wrapper(value)
}
""";

        var result = RunPipeline(source, "nested_constructor_value_mismatch.eidos", CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Buffer", StringComparison.Ordinal) ||
                          diagnostic.Message.Contains("Wrapper", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_Types_ExplicitFunctionValueArgumentSpecializesSignature()
    {
        const string source = """
Vector[comptime N: Int, comptime T: Type] :: type {
    Vector:: type(T)
}

identity[comptime N: Int, comptime T: Type] :: Vector[N, T] -> Vector[N, T]
{
    value => value
}

use :: Vector[4, Int] -> Vector[4, Int]
{
    value => identity[4, Int](value)
}
""";

        var result = RunPipeline(source, "explicit_function_value_generic.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var use = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var call = Assert.IsType<Eidosc.Ast.Expressions.CallExpr>(Assert.Single(use.Body).Expression);
        var application = Assert.IsType<Eidosc.Ast.Expressions.IndexExpr>(call.Function);
        Assert.Equal(2, application.GenericArguments.Count);
        Assert.IsType<Eidosc.Ast.Types.ValueGenericArgumentNode>(application.GenericArguments[0]);
        Assert.IsType<Eidosc.Ast.Types.TypeGenericArgumentNode>(application.GenericArguments[1]);
    }

    [Fact]
    public void CompilationPipeline_Types_ImplicitFunctionValueArgumentIsInferredFromParameterType()
    {
        const string source = """
Vector[comptime N: Int, comptime T: Type] :: type {
    Vector:: type(T)
}

identity[comptime N: Int, comptime T: Type] :: Vector[N, T] -> Vector[N, T]
{
    value => value
}

use :: Vector[4, Int] -> Vector[4, Int]
{
    value => identity(value)
}
""";

        var result = RunPipeline(source, "implicit_function_value_generic.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Fact]
    public void CompilationPipeline_Types_UnconstrainedFunctionValueArgumentRequiresExplicitValue()
    {
        const string source = """
constant[comptime N: Int] :: Unit -> Int
{
    _ => N
}

bad :: Unit -> Int
{
    _ => constant(())
}
""";

        var result = RunPipeline(source, "unconstrained_function_value_generic.eidos", CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot infer compile-time value argument", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_Types_SingleValueGenericApplicationIsReinterpretedFromIndexSyntax()
    {
        const string source = """
constant[comptime N: Int] :: Unit -> Int
{
    _ => N
}

use :: Unit -> Int
{
    _ => constant[4](())
}
""";

        var result = RunPipeline(source, "single_value_generic_application.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var use = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var call = Assert.IsType<Eidosc.Ast.Expressions.CallExpr>(Assert.Single(use.Body).Expression);
        var application = Assert.IsType<Eidosc.Ast.Expressions.IndexExpr>(call.Function);
        Assert.True(application.IsTypeApplication);
        Assert.Null(application.Index);
        var valueArgument = Assert.IsType<Eidosc.Ast.Types.ValueGenericArgumentNode>(Assert.Single(application.GenericArguments));
        Assert.IsType<Eidosc.Ast.Expressions.LiteralExpr>(valueArgument.Expression);
    }

    [Fact]
    public void CompilationPipeline_Types_ValueGenericTypeAliasSubstitutesIntoTarget()
    {
        const string source = """
Buffer[comptime N: Int, comptime T: Type] :: type {
    Buffer:: type(T)
}

FixedBuffer[comptime N: Int, comptime T: Type] :: type = Buffer[N, T];

use :: FixedBuffer[4, Int] -> Buffer[4, Int]
{
    value => value
}
""";

        var result = RunPipeline(source, "value_generic_type_alias.eidos", CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var functionType = Assert.IsType<TyFun>(function.InferredType);
        var parameterType = Assert.IsType<TyCon>(Assert.Single(functionType.Params));
        var resultType = Assert.IsType<TyCon>(functionType.Result);

        Assert.Equal(resultType.Name, parameterType.Name);
        Assert.Equal(
            Assert.Single(resultType.ValueArgs).CanonicalHash,
            Assert.Single(parameterType.ValueArgs).CanonicalHash);
    }

    [Fact]
    public void CompilationPipeline_Namer_ValueGenericTraitArgumentSubstitutesIntoMethodSignature()
    {
        const string source = """
Buffer[comptime N: Int, comptime T: Type] :: type {
    Buffer:: type(T)
}

Sized[comptime N: Int] :: trait {
    make :: Self -> Buffer[N, Int]
}

Holder :: type {
    Holder:: type(Int)
}


SizedHolder :: instance Sized[4] {
    make :: Holder -> Buffer[4, Int] {
        value => Buffer[4, Int](1)
    }
}
""";

        var result = RunPipeline(source, "value_generic_trait_argument.eidos", CompilationPhase.Namer);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = Assert.NotNull(symbolTable.LookupTrait("Sized"));
        var holderId = Assert.NotNull(symbolTable.LookupType("Holder"));
        var holder = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(holderId));
        var impl = symbolTable.LookupImplForTrait(holder.TypeId, traitId, ["4"]);

        Assert.NotNull(impl);
        var valueKey = Assert.Single(impl!.TraitTypeArgKeys);
        Assert.NotNull(valueKey.ValueArgument);
        Assert.Equal("4", valueKey.ValueArgument!.Value.DisplayText);
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
    Lift:: type(F[Int])
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
    Wrap:: type(A)
}

UseK[K] :: type {
    UseK:: type(K[Box])
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
    Wrap:: type(A)
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
    Wrap:: type(A)
}

ApplyToInt[F: kind2] :: type {
    ApplyToInt:: type(F[Int])
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
    Wrap:: type(A)
}

Lift[F] :: type {
    Lift:: type(F[Int])
}

ApplyToInt[F: kind2] :: type {
    ApplyToInt:: type(F[Int])
}

UseK[K] :: type {
    UseK:: type(K[Box])
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
    Wrap:: type(A)
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
