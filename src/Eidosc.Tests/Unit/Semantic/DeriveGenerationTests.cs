using System;
using System.IO;
using Eidosc.Symbols;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Diagnostic;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class DeriveGenerationTests
{
    [Fact]
    public void CaseSpecificDerive_GeneratesAnExactCaseImplementation()
    {
        const string source = """
Choice :: type {
    Selected :: type derive Eq {},
    Unselected :: type {},
}
""";

        var result = Compile("derive_exact_case.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.FuncDef>(),
            function => function.Name == "eq" &&
                        function.Clauses.Any(clause => clause.ClauseKind == Eidosc.Ast.Declarations.DeclarationClauseKind.Impl));
        var signature = Assert.IsType<Eidosc.Ast.Types.ArrowType>(generated.Signature.Single());
        var firstParameter = Assert.IsType<Eidosc.Ast.Types.TypePath>(signature.ParamType);
        Assert.Equal("Selected", firstParameter.TypeName);
        Assert.Single(generated.Body);
    }

    [Fact]
    public void IntermediateCaseDerive_CoversOnlyItsDescendantConstructors()
    {
        const string source = """
Choice :: type {
    Active :: type derive Show {
        Selected :: type {},
        Pending :: type {},
    },
    Inactive :: type {},
}
""";

        var result = Compile("derive_intermediate_case.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.FuncDef>(),
            function => function.Name == "show" &&
                        function.Clauses.Any(clause => clause.ClauseKind == Eidosc.Ast.Declarations.DeclarationClauseKind.Impl));
        Assert.Equal(2, generated.Body.Count);
    }

    [Fact]
    public void DeriveCopy_SingleConstructor_Compiles()
    {
        const string source = """

Point :: type  derive Copy
{
    Point:: type(Int, Int)
}
""";
        var result = Compile("derive_copy_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var copyTraitId = Assert.IsType<SymbolId>(symbolTable.LookupTrait("Copy"));
        var pointId = Assert.IsType<SymbolId>(symbolTable.LookupType("Point"));
        var pointType = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(pointId));
        var copyImpl = Assert.IsType<ImplSymbol>(symbolTable.LookupImplForTrait(pointType.TypeId, copyTraitId));
        Assert.False(copyImpl.HasRuntimeMethods);
    }

    [Fact]
    public void DeriveCopy_PhantomGeneric_DoesNotRequireUnusedTypeParameter()
    {
        const string source = """
Handle[A] :: type derive Copy { handle :: RawPtr }
""";

        var result = Compile("derive_copy_phantom_generic.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var handleId = Assert.IsType<SymbolId>(symbolTable.LookupType("Handle"));
        var handle = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(handleId));
        var specialized = new TypeId(9001);
        var descriptors = new Dictionary<int, Eidosc.Types.TypeDescriptor>
        {
            [specialized.Value] = new Eidosc.Types.TypeDescriptor.TyCon(
                Eidosc.Types.TypeConstructorKey.FromSymbol(handleId),
                [new TypeId(Eidosc.Types.BaseTypes.StringId)])
        };
        var resolver = Eidosc.Types.CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable, descriptors);

        Assert.True(resolver(specialized));
    }

    [Fact]
    public void DeriveClone_SingleConstructor_Compiles()
    {
        const string source = """

Box :: type  derive Clone
{
    Box:: type(String)
}
""";
        var result = Compile("derive_clone_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveClone_GeneratesSharedReferenceReceiver()
    {
        const string source = """

Box :: type  derive Clone
{
    Box:: type(String)
}
""";
        var result = Compile("derive_clone_receiver.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.FuncDef>(),
            function => function.Name == "clone" &&
                        function.Clauses.Any(clause => clause.ClauseKind == Eidosc.Ast.Declarations.DeclarationClauseKind.Impl));
        var signature = Assert.IsType<Eidosc.Ast.Types.ArrowType>(generated.Signature.Single());
        var receiver = Assert.IsType<Eidosc.Ast.Types.TypePath>(signature.ParamType);
        Assert.Equal("Ref", receiver.TypeName);
        Assert.Single(receiver.TypeArgs);
    }

    [Fact]
    public void DeriveEq_SingleConstructor_Compiles()
    {
        const string source = """

Pair :: type  derive Eq
{
    Pair:: type(Int, Int)
}
""";
        var result = Compile("derive_eq_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveShow_SingleConstructor_Compiles()
    {
        const string source = """

Wrapper :: type  derive Show
{
    Wrapper:: type(Int)
}
""";
        var result = Compile("derive_show_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveCopy_MultiConstructor_Compiles()
    {
        const string source = """



Shape :: type  derive Copy derive Clone derive Show
{
    Circle:: type(Int) , Rect:: type(Int, Int)
}
""";
        var result = Compile("derive_copy_multi.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveEq_MultiConstructor_Compiles()
    {
        const string source = """



Color :: type  derive Eq derive Copy derive Clone
{
    Red :: type {} , Green :: type {} , Blue :: type {}
}
""";
        var result = Compile("derive_eq_multi.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveOrd_MultiConstructor_Compiles()
    {
        const string source = """




Ordering2 :: type  derive Eq derive Ord derive Copy derive Clone
{
    Less2 :: type {} , Equal2 :: type {} , Greater2 :: type {}
}
""";
        var result = Compile("derive_ord_multi.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveAllOnGenericType_Compiles()
    {
        const string source = """




Maybe[T] :: type  derive Copy derive Clone derive Eq derive Show
{
    Just:: type(T) , Nothing :: type {}
}
""";
        var result = Compile("derive_all_generic.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveUnsupportedTrait_ReportsDiagnostic()
    {
        const string source = """

Point :: type  derive Debug
{
    Point:: type(Int)
}
""";

        var result = Compile("derive_unsupported_trait.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message == DiagnosticMessages.DeriveUnsupportedTrait("Debug"));
    }

    [Fact]
    public void ConstructorBridgeFacts_GeneratesTraitImpl()
    {
        const string source = """
DirectionVector :: trait {
    dx :: Self -> Int
    dy :: Self -> Int
}

Direction :: type {
    North :: type {} ,
    South :: type {} ,
    East :: type {} ,
    West :: type {}
}

DirectionVectorDirection :: instance DirectionVector for Direction {
    North => { dx = 0, dy = -1 } |
    South => { dx = 0, dy = 1 } |
    East => { dx = 1, dy = 0 } |
    West => { dx = -1, dy = 0 }
}

read_dx :: Direction -> Int
{
    dir => dx(dir)
}
""";

        var result = CompileThroughTypeInference("derive_ctor_constants.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var directionId = symbolTable.LookupType("Direction");
        var traitId = symbolTable.LookupType("DirectionVector");
        Assert.True(directionId.HasValue);
        Assert.True(traitId.HasValue);
        var directionSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(directionId.Value));
        Assert.NotNull(symbolTable.LookupImplForTrait(directionSymbol.TypeId, traitId.Value));
    }

    [Fact]
    public void ConstructorBridgeFacts_GadtConstructors_GeneratesTraitImpl()
    {
        const string source = """
Axis :: type {
    Vertical :: type {} , Horizontal :: type {}
}

DirectionVector :: trait {
    dx :: Self -> Int
    dy :: Self -> Int
}

Direction[A] :: type {
    North :: type case Direction[Vertical] {},
    South :: type case Direction[Vertical] {},
    East :: type case Direction[Horizontal] {},
    West :: type case Direction[Horizontal] {}
}

DirectionVectorDirection[A] :: instance DirectionVector for Direction[A] {
    North => { dx = 0, dy = -1 } |
    South => { dx = 0, dy = 1 } |
    East => { dx = 1, dy = 0 } |
    West => { dx = -1, dy = 0 }
}

read_dx[A] :: Direction[A] -> Int
{
    dir => dx(dir)
}
""";

        var result = CompileThroughTypeInference("derive_ctor_constants_gadt.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void ConstructorBridgeFacts_SelfReturnConstructorValues_GeneratesTraitImpl()
    {
        const string source = """
DirectionFacts :: trait {
    opposite :: Self -> Self
}


Direction :: type  derive Eq
{
    North :: type {} ,
    South :: type {}
}

DirectionFactsDirection :: instance DirectionFacts for Direction {
    North => { opposite = South() } |
    South => { opposite = North() }
}

read_opposite :: Direction -> Direction
{
    dir => opposite(dir)
}
""";

        var result = CompileThroughTypeInference("derive_ctor_constants_self_return.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void ConstructorBridgeFacts_PathValueReferences_GeneratesTraitImpl()
    {
        const string source = """
import std.GameMath

Pos :: type = GameMath.IVec2;

DirectionFacts :: trait {
    delta :: Self -> Pos
}

Direction :: type {
    North :: type {} ,
    East :: type {}
}

DirectionFactsDirection :: instance DirectionFacts for Direction {
    North => { delta = GameMath.up_i } |
    East => { delta = GameMath.east_i }
}

read_delta :: Direction -> Pos
{
    dir => delta(dir)
}
""";

        var result = CompileThroughTypeInference("derive_ctor_constants_path_value.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void ConstructorBridgeFacts_MissingConstant_ReportsDiagnostic()
    {
        const string source = """
DirectionVector :: trait {
    dx :: Self -> Int
}

Direction :: type {
    North :: type {} ,
    South :: type {}
}

DirectionVectorDirection :: instance DirectionVector for Direction {
    North => { dx = 0 }
}
""";

        var result = Compile("bridge_ctor_missing_constant.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Constructor 'South' must provide associated constant 'dx'", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstructorBridgeFacts_DuplicateConstant_ReportsDiagnostic()
    {
        const string source = """
DirectionVector :: trait {
    dx :: Self -> Int
}

Direction :: type {
    North :: type {}
}

DirectionVectorDirection :: instance DirectionVector for Direction {
    North => { dx = 0, dx = 1 }
}
""";

        var result = Compile("bridge_ctor_duplicate_constant.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message == DiagnosticMessages.ConstructorConstantDuplicate("North", "dx"));
    }

    [Fact]
    public void ConstructorBridgeFacts_UnsupportedTraitMethod_ReportsDiagnostic()
    {
        const string source = """
DirectionVector :: trait {
    dx :: Int -> Int
}

Direction :: type {
    North :: type {}
}

DirectionVectorDirection :: instance DirectionVector for Direction {
    North => { dx = 0 }
}
""";

        var result = Compile("bridge_ctor_unsupported_method.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("cannot be bridged from constructors", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstructorConstantSyntaxInType_ReportsDiagnostic()
    {
        const string source = """
Direction :: type {
    North :: type {} :: type:: type{ dx = 0 }
}
""";

        var result = Compile("removed_ctor_constant_syntax.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("expected a field declaration", StringComparison.Ordinal));
    }

    [Fact]
    public void DeriveOnEmptyProduct_UsesSyntheticConstructor()
    {
        const string source = """

Empty :: type  derive Eq
{
}
""";

        var result = Compile("derive_empty_type.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.FuncDef>(),
            function => function.Name == "eq" &&
                        function.Clauses.Any(clause =>
                            clause.ClauseKind == Eidosc.Ast.Declarations.DeclarationClauseKind.Impl));
        Assert.Single(generated.Body);
    }

    [Fact]
    public void DeriveCopy_GeneratesCopyMarkerFunction()
    {
        const string source = """

Unit2 :: type  derive Copy
{
    Unit2 :: type {}
}
""";
        // Derive-generated @impl(Copy) functions are resolved during type inference,
        // so we compile through to that phase. The compilation must succeed.
        var result = CompileThroughTypeInference("derive_copy_impl.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void TraitImpl_GenericConstraint_RegistersImpl()
    {
        const string source = """
MyClone :: trait {
    my_clone :: Self -> Self
}

Wrapper[T] :: type {
    Wrap:: type(T)
}


my_clone[T: MyClone] :: Wrapper[T] -> Wrapper[T]
 impl MyClone
{
    Wrap(v) => Wrap(my_clone(v))
}
""";
        var result = CompileThroughTypeInference("impl_generic_constraint.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var wrapperId = symbolTable.LookupType("Wrapper");
        Assert.True(wrapperId.HasValue);

        var myCloneTraitId = symbolTable.LookupType("MyClone");
        Assert.True(myCloneTraitId.HasValue);

        var wrapperSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(wrapperId.Value));
        var impl = symbolTable.LookupImplForTrait(wrapperSymbol.TypeId, myCloneTraitId.Value);
        Assert.NotNull(impl);
    }

    [Fact]
    public void DeriveClone_MultiConstructor_Compiles()
    {
        const string source = """


Result2[T, E] :: type  derive Clone derive Show
{
    Ok:: type(T) , Err:: type(E)
}
""";
        var result = CompileThroughTypeInference("derive_clone_result.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveEq_BareProductType_SynthesizesDefaultConstructor()
    {
        // Bare product type with no explicit constructor: the default constructor
        // is synthesized before derive processing, so @derive must behave exactly
        // like the equivalent explicit single-constructor form.
        const string source = """


Point :: type  derive Eq derive Show
{
    x:: Int,
    y:: Int
}
""";
        var result = CompileThroughTypeInference("derive_eq_bare_product.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var pointId = symbolTable.LookupType("Point");
        Assert.True(pointId.HasValue);

        var pointSymbol = Assert.IsAssignableFrom<AdtSymbol>(symbolTable.GetSymbol(pointId.Value));
        Assert.Single(pointSymbol.Constructors);
    }

    private static CompilationResult Compile(string fileName, string source)
    {
        return CompileWithTemporaryInput(fileName, WithStdTraitImports(source), CompilationPhase.Namer);
    }

    private static CompilationResult CompileThroughTypeInference(string fileName, string source)
    {
        return CompileWithTemporaryInput(fileName, WithStdTraitImports(source), CompilationPhase.Types);
    }

    private static CompilationResult CompileWithTemporaryInput(
        string fileName,
        string source,
        CompilationPhase stopAt)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_derive_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputFile = Path.Combine(tempDir, fileName);
        File.WriteAllText(inputFile, source);

        try
        {
            return new CompilationPipeline(source, new CompilationOptions
            {
                InputFile = inputFile,
                StopAtPhase = stopAt,
                UseColors = false
            }).Run();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string WithStdTraitImports(string source)
    {
        return """
import std.Traits
import std.TraitInvoke
import std.Ordering

""" + source;
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        if (result.Success)
            return "Success";

        var errors = result.Diagnostics
            .Where(d => d.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error)
            .Select(d => $"{d.Code}: {d.Message}");
        return string.Join("; ", errors);
    }
}
