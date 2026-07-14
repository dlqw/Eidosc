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
    public void DeriveCopy_SingleConstructor_Compiles()
    {
        const string source = """
@derive(Copy)
Point :: type {
    Point(Int, Int)
}
""";
        var result = Compile("derive_copy_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveClone_SingleConstructor_Compiles()
    {
        const string source = """
@derive(Clone)
Box :: type {
    Box(String)
}
""";
        var result = Compile("derive_clone_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveEq_SingleConstructor_Compiles()
    {
        const string source = """
@derive(Eq)
Pair :: type {
    Pair(Int, Int)
}
""";
        var result = Compile("derive_eq_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveShow_SingleConstructor_Compiles()
    {
        const string source = """
@derive(Show)
Wrapper :: type {
    Wrapper(Int)
}
""";
        var result = Compile("derive_show_single.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveCopy_MultiConstructor_Compiles()
    {
        const string source = """
@derive(Copy)
@derive(Clone)
@derive(Show)
Shape :: type {
    Circle(Int) , Rect(Int, Int)
}
""";
        var result = Compile("derive_copy_multi.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveEq_MultiConstructor_Compiles()
    {
        const string source = """
@derive(Eq)
@derive(Copy)
@derive(Clone)
Color :: type {
    Red , Green , Blue
}
""";
        var result = Compile("derive_eq_multi.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveOrd_MultiConstructor_Compiles()
    {
        const string source = """
@derive(Eq)
@derive(Ord)
@derive(Copy)
@derive(Clone)
Ordering2 :: type {
    Less2 , Equal2 , Greater2
}
""";
        var result = Compile("derive_ord_multi.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveAllOnGenericType_Compiles()
    {
        const string source = """
@derive(Copy)
@derive(Clone)
@derive(Eq)
@derive(Show)
Maybe[T] :: type {
    Just(T) , Nothing
}
""";
        var result = Compile("derive_all_generic.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveUnsupportedTrait_ReportsDiagnostic()
    {
        const string source = """
@derive(Debug)
Point :: type {
    Point(Int)
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
    North ,
    South ,
    East ,
    West
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
    Vertical , Horizontal
}

DirectionVector :: trait {
    dx :: Self -> Int
    dy :: Self -> Int
}

Direction[A] :: type {
    North -> Direction[Vertical] ,
    South -> Direction[Vertical] ,
    East -> Direction[Horizontal] ,
    West -> Direction[Horizontal]
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

@derive(Eq)
Direction :: type {
    North ,
    South
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
import Std.GameMath

Pos :: type = GameMath.IVec2;

DirectionFacts :: trait {
    delta :: Self -> Pos
}

Direction :: type {
    North ,
    East
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
    North ,
    South
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
    North
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
    North
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
    North { dx = 0 }
}
""";

        var result = Compile("removed_ctor_constant_syntax.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("constructor named blocks no longer accept", StringComparison.Ordinal));
    }

    [Fact]
    public void DeriveOnTypeWithoutConstructors_ReportsDiagnostic()
    {
        const string source = """
@derive(Eq)
Empty :: type {
}
""";

        var result = Compile("derive_empty_type.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message == DiagnosticMessages.DeriveTypeHasNoConstructors("Eq", "Empty"));
    }

    [Fact]
    public void DeriveCopy_GeneratesCopyMarkerFunction()
    {
        const string source = """
@derive(Copy)
Unit2 :: type {
    Unit2
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
    Wrap(T)
}

@impl(MyClone)
my_clone[T: MyClone] :: Wrapper[T] -> Wrapper[T]
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
@derive(Clone)
@derive(Show)
Result2[T, E] :: type {
    Ok(T) , Err(E)
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
@derive(Eq)
@derive(Show)
Point :: type {
    x: Int,
    y: Int
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
import Std.Trait
import Std.TraitInvoke
import Std.Ordering

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
