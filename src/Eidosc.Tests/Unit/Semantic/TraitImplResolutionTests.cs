using Eidosc.Symbols;
using System.Reflection;
using Eidosc;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class TraitImplResolutionTests
{
    [Fact]
    public void CompilationPipeline_NameFirstAssociatedTypeProjection_ResolvesInTraitSignature()
    {
        const string source = """
Iterator[I] :: trait
{
    Item :: type
    head :: I -> Iterator[I].Item
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_associated_projection_ok.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void CompilationPipeline_NameFirstAssociatedTypeProjection_ConcreteInstance_ReducesToAssociatedType()
    {
        const string source = """
Iterator[I] :: trait
{
    Item :: type
}

IteratorInt :: instance Iterator[Int]
{
    Item :: type = String
}

read :: Unit -> Iterator[Int].Item
{
    _ => "ok"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_associated_projection_concrete.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var read = Assert.Single(EnumerateAst(result.Ast).OfType<FuncDef>(), function => function.Name == "read");
        var functionType = Assert.IsType<TyFun>(read.InferredType);
        var returnType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal(WellKnownStrings.BuiltinTypes.String, returnType.Name);
    }

    [Fact]
    public void CompilationPipeline_AssociatedTypeProjectionSnapshot_RestoresStableProjectionQueries()
    {
        const string source = """
Iterator[I] :: trait
{
    Item :: type
}

IteratorInt :: instance Iterator[Int]
{
    Item :: type = String
}

read :: Unit -> Iterator[Int].Item
{
    _ => "ok"
}

read_again :: Unit -> Iterator[Int].Item
{
    _ => "ok"
}
""";

        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            PreviousAssociatedTypeProjectionSnapshot = first.AssociatedTypeProjectionSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.AssociatedTypeProjectionSnapshot);
        Assert.NotNull(second.AssociatedTypeProjectionSnapshot);
        Assert.True(first.AssociatedTypeProjectionSnapshot!.Entries.Count > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.hits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.restoreHits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionCache.hits") > 0);

        var read = Assert.Single(EnumerateAst(second.Ast).OfType<FuncDef>(), function => function.Name == "read");
        var functionType = Assert.IsType<TyFun>(read.InferredType);
        var returnType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal(WellKnownStrings.BuiltinTypes.String, returnType.Name);
    }

    [Fact]
    public void CompilationPipeline_AssociatedTypeProjectionSnapshot_DoesNotRestoreStaleValueType()
    {
        const string firstSource = """
Iterator[I] :: trait
{
    Item :: type
}

IteratorInt :: instance Iterator[Int]
{
    Item :: type = String
}

read :: Unit -> Iterator[Int].Item
{
    _ => "ok"
}
""";

        const string secondSource = """
Iterator[I] :: trait
{
    Item :: type
}

IteratorInt :: instance Iterator[Int]
{
    Item :: type = Int
}

read :: Unit -> Iterator[Int].Item
{
    _ => 1
}
""";

        var first = new CompilationPipeline(firstSource, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_stale_first.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_stale_second.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            PreviousAssociatedTypeProjectionSnapshot = first.AssociatedTypeProjectionSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.hits") > 0);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.restoreHits"));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.restoreStaleValueSignatures") > 0);

        var read = Assert.Single(EnumerateAst(second.Ast).OfType<FuncDef>(), function => function.Name == "read");
        var functionType = Assert.IsType<TyFun>(read.InferredType);
        var returnType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal(WellKnownStrings.BuiltinTypes.Int, returnType.Name);
    }

    [Fact]
    public void CompilationPipeline_AssociatedTypeProjectionSnapshot_RestoresNestedProjectionTypes()
    {
        const string source = """
Iterator[I] :: trait
{
    Item :: type
}

Box[A] :: type
{
    Box:: type(A)
}

IteratorInt :: instance Iterator[Int]
{
    Item :: type = Box[String]
}

read :: Unit -> Iterator[Int].Item
{
    _ => Box("ok")
}
""";

        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_nested_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_nested_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            PreviousAssociatedTypeProjectionSnapshot = first.AssociatedTypeProjectionSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.restoreHits") > 0);

        var read = Assert.Single(EnumerateAst(second.Ast).OfType<FuncDef>(), function => function.Name == "read");
        var functionType = Assert.IsType<TyFun>(read.InferredType);
        var returnType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal("Box", returnType.Name);
        var typeArg = Assert.Single(returnType.Args);
        Assert.Equal(WellKnownStrings.BuiltinTypes.String, Assert.IsType<TyCon>(typeArg).Name);
    }

    [Fact]
    public void CompilationPipeline_AssociatedTypeProjectionSnapshot_RestoresAliasSubstitutionShape()
    {
        const string source = """
Iterator[I] :: trait
{
    Item :: type
}

Pair[A, B] :: type
{
    Pair:: type(A, B)
}

PairWithRight[R, L] :: type = Pair[L, R]

IteratorInt :: instance Iterator[Int]
{
    Item :: type = PairWithRight[String, Int]
}

read :: Unit -> Iterator[Int].Item
{
    _ => Pair(1, "ok")
}
""";

        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_alias_substitution_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_type_projection_snapshot_alias_substitution_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            PreviousAssociatedTypeProjectionSnapshot = first.AssociatedTypeProjectionSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        var entry = Assert.Single(first.AssociatedTypeProjectionSnapshot!.Entries);
        Assert.NotNull(entry.ReducedTypeShape);
        Assert.StartsWith("type:", entry.ReducedTypeShape!.CanonicalKey, StringComparison.Ordinal);
        Assert.Equal("Pair", entry.ReducedTypeShape.Name);
        Assert.Equal(2, entry.ReducedTypeShape.Args.Count);

        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.hits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.restoreHits") > 0);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.associatedTypeProjectionPreviousCache.restoreMissingCanonicalShape"));

        var read = Assert.Single(EnumerateAst(second.Ast).OfType<FuncDef>(), function => function.Name == "read");
        var functionType = Assert.IsType<TyFun>(read.InferredType);
        var returnType = Assert.IsType<TyCon>(functionType.Result);
        Assert.Equal("Pair", returnType.Name);
        Assert.Collection(
            returnType.Args,
            firstArg => Assert.Equal(WellKnownStrings.BuiltinTypes.Int, Assert.IsType<TyCon>(firstArg).Name),
            secondArg => Assert.Equal(WellKnownStrings.BuiltinTypes.String, Assert.IsType<TyCon>(secondArg).Name));
    }

    [Fact]
    public void CompilationPipeline_NameFirstAssociatedTypeProjection_UnknownMember_ReportsDiagnostic()
    {
        const string source = """
Iterator[I] :: trait
{
    Item :: type
    head :: I -> Iterator[I].Missing
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_associated_projection_missing.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("does not declare associated type 'Missing'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NameFirstAssociatedConstProjection_ConcreteInstance_LowersImplementationValue()
    {
        const string source = """
Bounded[T] :: trait
{
    Min :: T
}

BoundedInt :: instance Bounded[Int]
{
    Min :: Int = 7
}

main :: Unit -> Int
{
    _ => Bounded[Int].Min
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_associated_const_projection.eidos",
            StopAtPhase = CompilationPhase.Hir,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var associatedConst = Assert.Single(EnumerateAst(result.Ast).OfType<AssociatedConstExpr>());
        Assert.NotNull(associatedConst.ImplementationValue);
        var inferredType = Assert.IsType<TyCon>(associatedConst.InferredType);
        Assert.Equal(WellKnownStrings.BuiltinTypes.Int, inferredType.Name);
    }

    [Fact]
    public void CompilationPipeline_AssociatedConstProjectionSnapshot_RecordsStableProjectionQueries()
    {
        const string source = """
Bounded[T] :: trait
{
    Min :: T
}

BoundedInt :: instance Bounded[Int]
{
    Min :: Int = 7
}

first :: Unit -> Int
{
    _ => Bounded[Int].Min
}

second :: Unit -> Int
{
    _ => Bounded[Int].Min
}
""";

        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_const_projection_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "associated_const_projection_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableDetailedProfiling = true,
            PreviousAssociatedConstProjectionSnapshot = first.AssociatedConstProjectionSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.AssociatedConstProjectionSnapshot);
        Assert.NotNull(second.AssociatedConstProjectionSnapshot);
        Assert.True(first.AssociatedConstProjectionSnapshot!.Entries.Count > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedConstProjectionCache.hits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedConstProjectionPreviousCache.hits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.associatedConstProjectionPreviousCache.validatedHits") > 0);
    }

    [Fact]
    public void CompilationPipeline_NameFirstAssociatedConstProjection_UnknownMember_ReportsDiagnostic()
    {
        const string source = """
Bounded[T] :: trait
{
    Min :: T
}

main :: Unit -> Int
{
    _ => Bounded[Int].Max
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_associated_const_projection_missing.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("does not declare associated const 'Max'", StringComparison.Ordinal));
    }

    private static IEnumerable<EidosAstNode> EnumerateAst(EidosAstNode? node)
    {
        if (node == null)
        {
            yield break;
        }

        yield return node;

        switch (node)
        {
            case ModuleDecl module:
                foreach (var decl in module.Declarations)
                {
                    foreach (var child in EnumerateAst(decl))
                    {
                        yield return child;
                    }
                }
                break;

            case FuncDef func:
                foreach (var branch in func.Body)
                {
                    foreach (var child in EnumerateAst(branch.Expression))
                    {
                        yield return child;
                    }
                }
                break;

            case GivenExpr given:
                foreach (var child in EnumerateAst(given.Target))
                {
                    yield return child;
                }
                break;

            case CallExpr call:
                foreach (var child in EnumerateAst(call.Function))
                {
                    yield return child;
                }
                foreach (var arg in call.PositionalArgs)
                {
                    foreach (var child in EnumerateAst(arg))
                    {
                        yield return child;
                    }
                }
                break;

            case MethodCallExpr { ResolvedStaticExpression: not null } methodCall:
                foreach (var child in EnumerateAst(methodCall.ResolvedStaticExpression))
                {
                    yield return child;
                }
                break;
        }
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_UnknownTrait_ReportsDiagnostic()
    {
        const string source = """
Person :: type {
    Person:: type(String)
}


show :: Person -> String
 impl MissingTrait
{
    p => "person"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_missing.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("Undefined trait 'MissingTrait'"));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_MethodNameMismatch_ReportsDiagnostic()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person:: type(String)
}


display :: Person -> String
 impl Show
{
    p => "person"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_name_mismatch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("does not match trait"));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_SignatureMismatch_ReportsDiagnostic()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person:: type(String)
}


show :: Person -> Int
 impl Show
{
    p => 1
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_sig_mismatch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("signature mismatch"));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_SignatureMismatch_UsesEffectSetSyntaxInDiagnostic()
    {
        const string source = """
LoggerUser :: trait {
    act :: Self -> Unit need Logger
}

Person :: type {
    Person:: type(String)
}


act :: Person -> Unit
 impl LoggerUser
{
    p => p
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_sig_mismatch_effectful.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("signature mismatch"));
        Assert.Contains("need Logger", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("-->", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilationPipeline_ConventionImpl_RegistersWhenTraitMethodNameMatches()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person:: type(String)
}

show :: Person -> String
{
    p => "person"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_convention_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Show");
        var personId = symbolTable.LookupType("Person");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);

        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
        var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value);

        Assert.NotNull(impl);
    }

    [Fact]
    public void CompilationPipeline_ConventionImpl_SignatureMismatch_DoesNotRegister()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person:: type(String)
}

show :: Person -> Int
{
    p => 1
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_convention_sig_mismatch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Show");
        var personId = symbolTable.LookupType("Person");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);

        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
        var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value);

        Assert.Null(impl);
    }

    [Fact]
    public void CompilationPipeline_ConventionImpl_DoesNotInferFunctionImplForMarkerTrait()
    {
        const string source = """
Marker :: trait {}

Person :: type {}

unrelated :: Person -> Int {
    _ => 1
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_convention_marker.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Marker");
        var personId = symbolTable.LookupType("Person");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);
        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));

        Assert.Null(symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value));
    }

    [Fact]
    public void CompilationPipeline_ConventionImpl_GenericTrait_RequiresExplicitImplAttribute()
    {
        const string source = """
Functor[F: kind2] :: trait {
    fmap :: Self -> F[Int]
}

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}

fmap :: Person -> Box[Int]
{
    p => Box(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_convention_generic_requires_attr.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("@impl(Functor[...])", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_GenericImpl_RegistersForGenericAdt()
    {
        const string source = """
Eq :: trait {
    eq :: Self -> Self -> Bool
}

Option[A] :: type {
    None :: type {} , Some:: type(A)
}


eq[A] :: Option[A] -> Option[A] -> Bool
 impl Eq
{
    left => true
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_generic_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Eq");
        var optionId = symbolTable.LookupType("Option");
        Assert.True(traitId.HasValue);
        Assert.True(optionId.HasValue);

        var optionSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(optionId.Value));
        var impl = symbolTable.LookupImplForTrait(optionSymbol.TypeId, traitId.Value);

        Assert.NotNull(impl);
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_GenericImplWithConstrainedHead_TracksConditionalRequirements()
    {
        const string source = """
Eq :: trait {
    eq :: Self -> Self -> Bool
}

Option[A] :: type {
    None :: type {} , Some:: type(A)
}


eq[T: Eq] :: Option[T] -> Option[T] -> Bool
 impl Eq
{
    _ => _ => true
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_generic_conditional_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Eq");
        var optionId = symbolTable.LookupType("Option");
        Assert.True(traitId.HasValue);
        Assert.True(optionId.HasValue);

        var optionSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(optionId.Value));
        var impl = symbolTable.LookupImplForTrait(optionSymbol.TypeId, traitId.Value);

        Assert.NotNull(impl);
        var requirement = Assert.Single(impl.ImplementingTypeRequirements);
        Assert.Equal(0, requirement.TypeArgIndex);
        Assert.Equal("Eq", requirement.TraitName);
        Assert.Equal(traitId.Value, requirement.Trait);
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_SpecializedImplementingTypeOverlap_AllowsCoexistingImpls()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Option[A] :: type {
    None :: type {} , Some:: type(A)
}


show[T] :: Option[T] -> String
 impl Show
{
    _ => "generic"
}


show :: Option[Int] -> String
 impl Show
{
    _ => "int"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_specialization_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Show");
        var optionId = symbolTable.LookupType("Option");
        Assert.True(traitId.HasValue);
        Assert.True(optionId.HasValue);

        var optionSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(optionId.Value));
        var candidates = symbolTable.LookupImplCandidatesForTrait(optionSymbol.TypeId, traitId.Value);
        Assert.Equal(2, candidates.Count);
        Assert.Null(symbolTable.LookupImplForTrait(optionSymbol.TypeId, traitId.Value));

        var intImpl = symbolTable.LookupImplForTrait(optionSymbol.TypeId, traitId.Value, "Option[Int]");
        Assert.NotNull(intImpl);
        Assert.Equal("Option[Int]", intImpl!.CanonicalImplementingType);

        var genericImpl = symbolTable.LookupImplForTrait(optionSymbol.TypeId, traitId.Value, "Option[String]");
        Assert.NotNull(genericImpl);
        Assert.Equal("Option[T]", genericImpl!.CanonicalImplementingType);
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_GenericTraitTypeArgs_RegistersImplementation()
    {
        const string source = """
Functor[F: kind2] :: trait {
    fmap :: Self -> F[Int]
}

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}


fmap :: Person -> Box[Int]
 impl Functor[Box]
{
    p => Box(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_generic_trait_args_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Functor");
        var personId = symbolTable.LookupType("Person");
        var boxId = symbolTable.LookupType("Box");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);
        Assert.True(boxId.HasValue);

        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
        var boxSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(boxId.Value));
        var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value, ["Box"]);

        Assert.NotNull(impl);
        var canonicalTraitArgKey = Assert.Single(impl!.CanonicalTraitTypeArgKeys);
        Assert.Equal(boxId.Value, canonicalTraitArgKey.SymbolId);
        Assert.Equal(boxSymbol.TypeId, canonicalTraitArgKey.TypeId);
        Assert.Null(symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_NestedCanonicalTraitTypeArgKey_PreservesTypeArguments()
    {
        const string source = """
Wrapper[T] :: trait {
    get :: Self -> Int
}

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}


get :: Person -> Int
 impl Wrapper[Box[Int]]
{
    p => 1
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_nested_canonical_trait_arg_key.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Wrapper");
        var personId = symbolTable.LookupType("Person");
        var boxId = symbolTable.LookupType("Box");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);
        Assert.True(boxId.HasValue);

        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
        var boxSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(boxId.Value));
        var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value, ["Box[Int]"]);

        Assert.NotNull(impl);
        var canonicalTraitArgKey = Assert.Single(impl!.CanonicalTraitTypeArgKeys);
        Assert.Equal(boxId.Value, canonicalTraitArgKey.SymbolId);
        Assert.Equal(boxSymbol.TypeId, canonicalTraitArgKey.TypeId);
        var intArgKey = Assert.Single(canonicalTraitArgKey.TypeArguments);
        Assert.Equal(new TypeId(BaseTypes.IntId), intArgKey.TypeId);
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_GenericTraitTypeArgs_ArityMismatch_ReportsDiagnostic()
    {
        const string source = """
Functor[F: kind2] :: trait {
    fmap :: Self -> F[Int]
}

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}


fmap :: Person -> Box[Int]
 impl Functor[Int, Box]
{
    p => Box(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_generic_trait_args_arity_mismatch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("expects 1 type argument(s) in an impl clause, got 2", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_AliasOverlapOnImplementingType_ReportsDiagnostic()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person:: type(String)
}

PersonAlias :: type = Person;


show :: Person -> String
 impl Show
{
    p => "person"
}


show :: PersonAlias -> String
 impl Show
{
    p => "alias"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_alias_overlap_implementing_type.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E3004" &&
                    item.Message.Contains("Ambiguous overlapping impl registration", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requested impl head: @impl(Show) on PersonAlias", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("existing impl head: @impl(Show) on Person", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requested canonical head: @impl(Show) on Person", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("existing canonical head: @impl(Show) on Person", StringComparison.Ordinal));
        var related = Assert.Single(diagnostic.Related);
        Assert.Contains("existing overlapping impl registered here", related.Message, StringComparison.Ordinal);
        Assert.Contains(related.Labels, label => label.Message.Contains("@impl(Show) on Person", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_AliasOverlapOnMultipleTraitTypeArgs_ReportsDiagnostic()
    {
        const string source = """
Pairing[F: kind2, G: kind2] :: trait {
    build :: Self -> F[Int]
}

Person :: type {
    Person:: type(Int)
}

Result[T, E] :: type {
    Ok:: type(T) , Err:: type(E)
}

ResultWith[E, T] :: type = Result[T, E];
DeepResultWith[E, T] :: type = ResultWith[E, T];
AlsoResultWith[E, T] :: type = Result[T, E];

Box[A] :: type {
    Box:: type(A)
}


build :: Person -> DeepResultWith[String, Int]
 impl Pairing[DeepResultWith[String], Box]
{
    p => Ok(1)
}


build :: Person -> AlsoResultWith[String, Int]
 impl Pairing[AlsoResultWith[String], Box]
{
    p => Ok(2)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_alias_overlap_multi_trait_type_args.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E3004" &&
                    item.Message.Contains("Ambiguous overlapping impl registration", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requested impl head: @impl(Pairing[AlsoResultWith[String], Box]) on Person", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("existing impl head: @impl(Pairing[DeepResultWith[String], Box]) on Person", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requested canonical head: @impl(Pairing[Result[T,String], Box]) on Person", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("existing canonical head: @impl(Pairing[Result[T,String], Box]) on Person", StringComparison.Ordinal));
        var related = Assert.Single(diagnostic.Related);
        Assert.Contains("existing overlapping impl registered here", related.Message, StringComparison.Ordinal);
        Assert.Contains(related.Labels, label => label.Message.Contains("@impl(Pairing[DeepResultWith[String], Box]) on Person", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_QualifiedTraitPath_Registers()
    {
        const string source = """
M :: module {
    Show :: trait {
        show :: Self -> String
    }

    Person :: type {
        Person:: type(String)
    }


    show :: Person -> String
     impl M . Show
{
        p => "ok"
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_qualified_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Show");
        var personId = symbolTable.LookupType("Person");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);

        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
        var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value);

        Assert.NotNull(impl);
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_QualifiedTraitPath_FromImportedModuleFile_Registers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_trait_impl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleFile = Path.Combine(tempDir, "M.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
M :: module {
    Show :: trait {
        show :: Self -> String
    }
}
""";

        const string entrySource = """
import M

Person :: type {
    Person:: type(String)
}


show :: Person -> String
 impl M . Show
{
    p => "ok"
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                UseColors = false
            }).Run();

            Assert.True(result.Success);

            var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
            var traitResolution = symbolTable.ResolvePathWithResult(["M", "Show"]);
            Assert.True(traitResolution.IsSuccess);

            var personId = symbolTable.LookupType("Person");
            Assert.True(personId.HasValue);

            var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
            var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitResolution.SymbolId);

            Assert.NotNull(impl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_ImportedStdTrait_RegistersForUserType()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_trait_impl_std_trait_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var entryFile = Path.Combine(tempDir, "main.eidos");
        const string source = """
import std.Traits

Person :: type {
    Person:: type(String)
}


eq :: Person -> Person -> Bool
 impl Traits.Eq
{
    _ => _ => true
}
""";

        File.WriteAllText(entryFile, source);

        try
        {
            var result = new CompilationPipeline(source, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                UseColors = false
            }).Run();

            Assert.True(result.Success);

            var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
            var traitModuleId = symbolTable.Modules.LookupModuleByPath("std", ["Traits"]);
            Assert.True(traitModuleId.HasValue);
            Assert.True(symbolTable.Modules.TryLookupAccessibleBinding(
                traitModuleId.Value,
                "Eq",
                requesterModuleId: null,
                out var traitResolution));

            var personId = symbolTable.LookupType("Person");
            Assert.True(personId.HasValue);

            var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
            var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitResolution.SymbolId);

            Assert.NotNull(impl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_UnknownQualifiedTrait_ReportsSingleImplDiagnostic()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person:: type(String)
}


show :: Person -> String
 impl N . Show
{
    p => "ok"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_qualified_missing.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("Undefined trait 'N.Show' in @impl"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("Cannot resolve path 'N.Show'"));
    }

    [Fact]
    public void CompilationPipeline_TypeDeclarationNamedSelf_ReportsReservedDiagnostic()
    {
        const string source = """
Self :: type {
    Self:: type(Int)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "reserved_self_type.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("cannot be declared as a type"));
    }

    [Fact]
    public void CompilationPipeline_TraitDeclarationNamedSelf_ReportsReservedDiagnostic()
    {
        const string source = """
Self :: trait {
    show :: Self -> String
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "reserved_self_trait.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("cannot be declared as a trait"));
    }

    [Fact]
    public void CompilationPipeline_TypeParameterNamedSelf_ReportsReservedDiagnostic()
    {
        const string source = """
id[Self] :: Int -> Int
{
    x => x
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "reserved_self_type_param.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3000" && d.Message.Contains("cannot be declared as a type parameter"));
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_AllowsMethodLevelConstrainedTypeParamsOutsideImplHead()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Box[A] :: type {
    Box:: type(A)
}

Traversable[T: kind2] :: trait {
    traverse[A, B, G: kind2 : Applicative[G]] :: T[A] -> (A -> G[B]) -> G[T[B]]
}


traverse[A, B, G: kind2 : Applicative[G]] :: Box[A] -> (A -> G[B]) -> G[Box[B]]
 impl Traversable[Box]
{
    value => f => f(value)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_non_head_constrained_type_param.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
    }
}

