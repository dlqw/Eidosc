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
    public void SameNamedTypesInDifferentModules_GenerateDistinctDerivedInstanceNames()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_derive_modules_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var firstPath = Path.Combine(tempDir, "First.eidos");
        var secondPath = Path.Combine(tempDir, "Second.eidos");
        var mainPath = Path.Combine(tempDir, "Main.eidos");
        File.WriteAllText(firstPath, """
First :: module {
    @[derive(Copy)]
    export Status :: type { Ready :: type {} }
}
""");
        File.WriteAllText(secondPath, """
Second :: module {
    @[derive(Copy)]
    export Status :: type { Ready :: type {} }
}
""");
        File.WriteAllText(mainPath, """
Main :: module {
    import First
    import Second
}
""");

        try
        {
            var result = new CompilationPipeline(File.ReadAllText(mainPath), new CompilationOptions
            {
                InputFile = mainPath,
                StopAtPhase = CompilationPhase.Namer,
                ImportSearchRoots = [tempDir],
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.DoesNotContain(result.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("Duplicate instance declaration", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CaseSpecificDerive_GeneratesAnExactCaseImplementation()
    {
        const string source = """
Choice :: type {
    @[derive(Eq)]
    Selected :: type {},
    Unselected :: type {},
}
""";

        var result = Compile("derive_exact_case.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var instance = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>(),
            declaration => declaration.Trait?.TraitName == "Eq");
        var generated = Assert.Single(instance.Methods, function => function.Name == "eq");
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
    @[derive(Show)]
    Active :: type {
        Selected :: type {},
        Pending :: type {},
    },
    Inactive :: type {},
}
""";

        var result = Compile("derive_intermediate_case.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var instance = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>(),
            declaration => declaration.Trait?.TraitName == "Show");
        var generated = Assert.Single(instance.Methods, function => function.Name == "show");
        Assert.Equal(2, generated.Body.Count);
    }

    [Fact]
    public void DeriveCopy_SingleConstructor_Compiles()
    {
        const string source = """

@[derive(Copy)]

Point :: type
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
@[derive(Copy)]
Handle[A] :: type { handle :: RawPtr }
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

@[derive(Clone)]

Box :: type
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

@[derive(Clone)]

Box :: type
{
    Box:: type(String)
}
""";
        var result = Compile("derive_clone_receiver.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var instance = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>(),
            declaration => declaration.Trait?.TraitName == "Clone");
        var generated = Assert.Single(instance.Methods, function => function.Name == "clone");
        var signature = Assert.IsType<Eidosc.Ast.Types.ArrowType>(generated.Signature.Single());
        var receiver = Assert.IsType<Eidosc.Ast.Types.TypePath>(signature.ParamType);
        Assert.Equal("Ref", receiver.TypeName);
        Assert.Single(receiver.TypeArgs);
    }

    [Fact]
    public void DeriveEq_SingleConstructor_Compiles()
    {
        const string source = """

@[derive(Eq)]

Pair :: type
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

@[derive(Show)]

Wrapper :: type
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

@[derive(Copy, Clone, Show)]

Shape :: type
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

@[derive(Eq, Copy, Clone)]

Color :: type
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

@[derive(Eq, Ord, Copy, Clone)]

Ordering2 :: type
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

@[derive(Copy, Clone, Eq, Show)]

Maybe[T] :: type
{
    Just:: type(T) , Nothing :: type {}
}
""";
        var result = Compile("derive_all_generic.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveFunctionalTraits_GenericSum_CompilesThroughTypeInference()
    {
        const string source = """
@[derive(Functor, Foldable, Traversable)]
Maybe[A] :: type {
    Just :: type(A),
    Nothing :: type {}
}
""";

        var result = CompileThroughTypeInference("derive_functional_maybe.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var instances = result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>().ToList();
        Assert.Contains(instances, instance => instance.Trait?.TraitName == "Functor");
        Assert.Contains(instances, instance => instance.Trait?.TraitName == "Foldable");
        Assert.Contains(instances, instance => instance.Trait?.TraitName == "Traversable");
    }

    [Fact]
    public void DeriveFunctionalTraits_FixedPrefixAndPhantomFields_Compile()
    {
        const string source = """
@[derive(Functor, Foldable, Traversable)]
Validation[Error, Value] :: type {
    Invalid :: type(Error),
    Valid :: type(Value),
    Empty :: type {}
}
""";

        var result = CompileThroughTypeInference("derive_functional_fixed_prefix.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var functor = Assert.Single(result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>(),
            instance => instance.Trait?.TraitName == "Functor");
        var target = Assert.IsType<Eidosc.Ast.Types.TypePath>(Assert.Single(functor.Trait!.TypeArgs));
        Assert.Equal("Validation", target.TypeName);
        Assert.Single(target.TypeArgs);
        Assert.Equal("Error", Assert.IsType<Eidosc.Ast.Types.TypePath>(target.TypeArgs[0]).TypeName);
    }

    [Fact]
    public void DeriveFunctionalTraits_NamedAndNestedFields_Compile()
    {
        const string source = """
@[derive(Functor, Foldable, Traversable)]
Envelope[A] :: type {
    payload :: Seq[Option[A]],
    revision :: Int
}
""";

        var result = CompileThroughTypeInference("derive_functional_nested_named.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveFunctionalTraits_NestedHigherKindParameter_AddsConstraint()
    {
        const string source = """
@[derive(Functor, Foldable, Traversable)]
Higher[F: kind2, A] :: type { Higher :: type(F[A]) }
""";

        var result = CompileThroughTypeInference("derive_functional_nested_hkt.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var functor = Assert.Single(result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>(),
            instance => instance.Trait?.TraitName == "Functor");
        var f = Assert.Single(functor.TypeParams, parameter => parameter.Name == "F");
        Assert.Contains(f.TraitConstraints, constraint => constraint.TraitName == "Functor");
    }

    [Fact]
    public void DeriveFunctionalTraits_RecursiveTree_Compiles()
    {
        const string source = """
@[derive(Functor, Foldable, Traversable)]
Tree[A] :: type {
    Leaf :: type(A),
    Branch :: type(Tree[A], Tree[A])
}
""";

        var result = CompileThroughTypeInference("derive_functional_recursive_tree.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void DeriveFunctionalTraits_GeneratedSignaturePreservesEffectsAndKinds()
    {
        const string source = """
@[derive(Traversable)]
Box[A] :: type { Box :: type(A) }
""";

        var result = Compile("derive_traversable_signature.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var instance = Assert.Single(result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>(),
            declaration => declaration.Trait?.TraitName == "Traversable");
        var traverse = Assert.Single(instance.Methods);
        Assert.Contains(traverse.TypeParams, parameter => parameter.Name == "G" && parameter.GetKindArity() == 1);
        Assert.Contains(traverse.TypeParams, parameter => parameter.Name == "E" && parameter.IsEffectSet);
        Assert.Single(traverse.RequiredAbilities, requirement => requirement.Path.SequenceEqual(["E"]));
        var receiverArrow = Assert.IsType<Eidosc.Ast.Types.ArrowType>(Assert.Single(traverse.Signature));
        var callbackArrow = Assert.IsType<Eidosc.Ast.Types.ArrowType>(receiverArrow.ReturnType);
        var callback = Assert.IsType<Eidosc.Ast.Types.ArrowType>(callbackArrow.ParamType);
        Assert.Single(callback.RequiredEffects, requirement => requirement.Path.SequenceEqual(["E"]));
    }

    [Fact]
    public void DeriveFunctionalTrait_RequiresFinalOrdinaryTypeParameter()
    {
        const string source = """
@[derive(Functor)]
Token :: type { Token :: type(Int) }
""";

        var result = Compile("derive_functor_no_parameter.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message ==
            DiagnosticMessages.DeriveFunctionalTypeParameterRequired("Functor", "Token"));
    }

    [Fact]
    public void DeriveFunctionalTrait_ReportsNonFinalOccurrenceAtField()
    {
        const string source = """
@[derive(Functor)]
Bad[A] :: type { Bad :: type(Result[A, Int]) }
""";

        var result = Compile("derive_functor_non_final.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("constructor 'Bad' field '#1'", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("non-final type argument", StringComparison.Ordinal));
    }

    [Fact]
    public void DeriveUnsupportedTrait_ReportsDiagnostic()
    {
        const string source = """

@[derive(Debug)]

Point :: type
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

@[derive(Eq)]

Direction :: type
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

@[derive(Eq)]

Empty :: type
{
}
""";

        var result = Compile("derive_empty_type.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var instance = Assert.Single(
            result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.InstanceDecl>(),
            declaration => declaration.Trait?.TraitName == "Eq");
        var generated = Assert.Single(instance.Methods, function => function.Name == "eq");
        Assert.Single(generated.Body);
    }

    [Fact]
    public void DeriveCopy_GeneratesCopyMarkerInstance()
    {
        const string source = """

@[derive(Copy)]

Unit2 :: type
{
    Unit2 :: type {}
}
""";
        // Derive-generated Copy evidence is a method-free named instance.
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

MyCloneWrapper :: instance MyClone {
    my_clone[T: MyClone] :: Wrapper[T] -> Wrapper[T]
    {
        Wrap(v) => Wrap(my_clone(v))
    }
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

@[derive(Clone, Show)]

Result2[T, E] :: type
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

@[derive(Eq, Show)]

Point :: type
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
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [WellKnownStrings.Std.Module] = []
                }
            }).Run();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string WithStdTraitImports(string source)
    {
        return source;
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
