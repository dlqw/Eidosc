using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_NestedClosedCase_ExposesIntermediateEffectiveFieldsAndInjectsToEveryAncestor()
    {
        const string source = """
Anim :: type {
    name :: String,

    Mammal :: type {
        warm :: Bool,

        Dog :: type {
            breed :: String,
        },

        Cat :: type {},
    },
}

read_mammal :: Anim.Mammal -> Bool
{
    mammal => mammal.warm
}

read_dog :: Anim.Mammal.Dog -> String
{
    dog => dog.breed
}

as_mammal :: Anim.Mammal.Dog -> Anim.Mammal
{
    dog => dog
}

as_anim :: Anim.Mammal.Dog -> Anim
{
    dog => dog
}

choose_mammal :: Bool -> Anim.Mammal
{
    value => if value then Dog { name: "Nori", warm: true, breed: "Shiba" }
             else Cat { name: "Mochi", warm: true }
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var mir = Assert.IsType<MirModule>(result.MirModule);
        Assert.True(
            mir.Functions
                .SelectMany(static function => function.BasicBlocks)
                .SelectMany(static block => block.Instructions)
                .OfType<MirCaseInject>()
                .Count() >= 2);
    }

    [Fact]
    public void Types_ClosedCaseSubtype_DoesNotLiftThroughGenericOrReferencesAndNeverDowncasts()
    {
        const string source = """
Anim :: type {
    Dog :: type {},
}

Box[T] :: type {
    value :: T,
}

downcast :: Anim -> Anim.Dog
{
    anim => anim
}

generic_lift :: Box[Anim.Dog] -> Box[Anim]
{
    box => box
}

ref_lift :: Ref[Anim.Dog] -> Ref[Anim]
{
    dog => dog
}

mref_lift :: MRef[Anim.Dog] -> MRef[Anim]
{
    dog => dog
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.True(
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == "E4000") >= 4,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_FreshClosedCaseArgumentThroughGenericCall_WidensBeforeInvariantEmbedding()
    {
        const string source = """
Anim :: type {
    Dog :: type {},
}

Box[T] :: type {
    value :: T,
}

Holder :: type {
    box :: Box[Anim],
}

singleton[T] :: T -> Box[T]
{
    value => Box{value: value}
}

make :: Unit -> Holder
{
    _ => Holder{box: singleton(Dog())}
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_AssignmentContext_WidensFreshClosedCaseBeforeInvariantEmbedding()
    {
        const string source = """
Anim :: type {
    Dog :: type {},
}

Box[T] :: type {
    value :: T,
}

make :: Unit -> Box[Anim]
{
    _ => {
        mut result: Box[Anim] := Box{value: Dog()};
        result = Box{value: Dog()};
        result
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_ClosedCaseTraitEvidence_IsNotInheritedFromParentInstance()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Anim :: type {
    Dog :: type {},
}

MarkerAnim :: instance Marker {
    mark :: Anim -> Bool
    {
        _ => true
    }
}

require_marker[T: Marker] :: T -> T
{
    value => value
}

bad :: Unit -> Anim.Dog
{
    _ => require_marker(Dog())
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E2001" &&
            diagnostic.Message.Contains("Marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ClosedCaseTraitEvidence_UsesExplicitCaseInstance()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Anim :: type {
    Dog :: type {},
}

MarkerAnim :: instance Marker {
    mark :: Anim -> Bool
    {
        _ => true
    }
}

MarkerDog :: instance Marker {
    mark :: Anim.Dog -> Bool
    {
        _ => false
    }
}

require_marker[T: Marker] :: T -> T
{
    value => value
}

good :: Unit -> Anim.Dog
{
    _ => require_marker(Dog())
}

read_anim :: Anim -> Bool
{
    value => value.mark()
}

read_dog :: Anim.Dog -> Bool
{
    value => value.mark()
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ClosedCaseTraitAssociatedItems_DispatchByExactStaticType()
    {
        const string source = """
View[T] :: trait {
    Item :: type
    Tag :: Int
}

Anim :: type {
    Dog :: type {},
}

ViewAnim :: instance View[Anim] {
    Item :: type = Int
    Tag :: Int = 1
}

ViewDog :: instance View[Anim.Dog] {
    Item :: type = String
    Tag :: Int = 2
}

read_anim_item :: Unit -> View[Anim].Item
{
    _ => 1
}

read_dog_item :: Unit -> View[Anim.Dog].Item
{
    _ => "dog"
}

read_anim_tag :: Unit -> Int
{
    _ => View[Anim].Tag
}

read_dog_tag :: Unit -> Int
{
    _ => View[Anim.Dog].Tag
}
""";

        var result = RunPipeline(source, CompilationPhase.Hir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var readAnim = Assert.Single(result.Ast!.Declarations.OfType<FuncDef>(), static function => function.Name == "read_anim_item");
        var readDog = Assert.Single(result.Ast.Declarations.OfType<FuncDef>(), static function => function.Name == "read_dog_item");
        Assert.Equal("Int", Assert.IsType<TyCon>(Assert.IsType<TyFun>(readAnim.InferredType).Result).Name);
        Assert.Equal("String", Assert.IsType<TyCon>(Assert.IsType<TyFun>(readDog.InferredType).Result).Name);
        var associatedConstEntries = Assert.IsType<AssociatedConstProjectionSnapshot>(result.AssociatedConstProjectionSnapshot).Entries;
        Assert.Equal(2, associatedConstEntries.Count);
        Assert.Equal(2, associatedConstEntries.Select(static entry => entry.ImplementingTypeKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, associatedConstEntries.Select(static entry => entry.ConstValueSignature).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Types_ClosedCaseJoin_RejectsIncompatibleGadtParentSpecializations()
    {
        const string source = """
Expr[T] :: type {
    IntLit :: type(Int) case Expr[Int],
    BoolLit :: type(Bool) case Expr[Bool],
}

bad_join :: Bool -> Unit
{
    choose => {
        value := if choose then IntLit(1) else BoolLit(false);
        ()
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E4000" &&
            diagnostic.Message.Contains("Cannot unify", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ClosedCaseJoin_UsesSymbolIdentityForSameNamedLeavesInDifferentBranches()
    {
        const string source = """
Tree :: type {
    Left :: type {
        Leaf :: type {},
    },

    Right :: type {
        Leaf :: type {},
    },
}

choose_leaf :: Bool -> Tree
{
    choose => {
        value := if choose then Tree.Left.Leaf() else Tree.Right.Leaf();
        value
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var tree = Assert.Single(result.SymbolTable!.Symbols.Values.OfType<AdtSymbol>(), static symbol =>
            !symbol.IsCaseType && string.Equals(symbol.Name, "Tree", StringComparison.Ordinal));
        var injections = result.TypeInferer!.GetClosedCaseInjectionSnapshot();
        Assert.Equal(2, injections.Count(injection => injection.Value.TargetAncestor == tree.Id));
        Assert.Equal(
            2,
            injections
                .Where(injection => injection.Value.TargetAncestor == tree.Id)
                .Select(static injection => injection.Value.SourceCase)
                .Distinct()
                .Count());
    }

    [Fact]
    public void Types_NestedClosedCaseGenericIdentity_CapturesRootAndEveryCasePathArgument()
    {
        const string source = """
Envelope[T] :: type {
    root :: T,

    Branch[A] :: type {
        branch :: A,

        Leaf[B] :: type {
            leaf :: B,
        },
    },
}

make_leaf :: Unit -> Envelope[Int].Branch[String].Leaf[Bool]
{
    _ => Leaf { root: 1, branch: "value", leaf: true }
}

read_branch :: Envelope[Int].Branch[String] -> String
{
    branch => branch.branch
}

read_leaf :: Envelope[Int].Branch[String].Leaf[Bool] -> Bool
{
    leaf => leaf.leaf
}

as_branch :: Envelope[Int].Branch[String].Leaf[Bool] -> Envelope[Int].Branch[String]
{
    leaf => leaf
}

as_root :: Envelope[Int].Branch[String].Leaf[Bool] -> Envelope[Int]
{
    leaf => leaf
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_NestedClosedCaseValueGenericIdentity_CapturesEveryPathArgumentAndFieldIdentity()
    {
        const string source = """
Envelope[comptime N: Int] :: type {
    root :: Int,

    Branch[comptime M: Int] :: type {
        branch :: Int,

        Leaf[comptime K: Int] :: type {
            leaf :: Int,
        },
    },
}

make_leaf :: Unit -> Envelope[1].Branch[2].Leaf[3]
{
    _ => Leaf { root: 1, branch: 2, leaf: 3 }
}

as_branch :: Envelope[1].Branch[2].Leaf[3] -> Envelope[1].Branch[2]
{
    leaf => leaf
}

read_leaf :: Envelope[1].Branch[2].Leaf[3] -> Int
{
    value => value.leaf
}

read_other :: Envelope[1].Branch[4].Leaf[3] -> Int
{
    value => value.leaf
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var root = Assert.Single(result.Ast!.Declarations.OfType<AdtDef>());
        var branch = Assert.Single(root.Cases);
        var leaf = Assert.Single(branch.Cases);
        Assert.All(root.Fields.Concat(branch.Fields).Concat(leaf.Fields), static field => Assert.True(field.SymbolId.IsValid));
        Assert.Equal(
            3,
            root.Fields.Concat(branch.Fields).Concat(leaf.Fields).Select(static field => field.SymbolId).Distinct().Count());
        Assert.Equal(root.Fields.Select(static field => field.SymbolId), result.SymbolTable!.GetSymbol<AdtSymbol>(root.SymbolId)!.Fields);
        Assert.Equal(branch.Fields.Select(static field => field.SymbolId), result.SymbolTable.GetSymbol<AdtSymbol>(branch.SymbolId)!.Fields);
        Assert.Equal(leaf.Fields.Select(static field => field.SymbolId), result.SymbolTable.GetSymbol<AdtSymbol>(leaf.SymbolId)!.Fields);

        var constructor = Assert.Single(root.Constructors);
        var constructorSymbol = Assert.IsType<CtorSymbol>(result.SymbolTable.GetSymbol(constructor.SymbolId));
        Assert.Contains("Envelope.Branch.Leaf", constructorSymbol.SignatureText, StringComparison.Ordinal);
        Assert.Contains("N", constructorSymbol.SignatureText, StringComparison.Ordinal);
        Assert.Contains("M", constructorSymbol.SignatureText, StringComparison.Ordinal);
        Assert.Contains("K", constructorSymbol.SignatureText, StringComparison.Ordinal);

        var readLeaf = Assert.Single(result.Ast.Declarations.OfType<FuncDef>(), static function => function.Name == "read_leaf");
        var readOther = Assert.Single(result.Ast.Declarations.OfType<FuncDef>(), static function => function.Name == "read_other");
        var leafParameter = Assert.Single(Assert.IsType<TyFun>(readLeaf.InferredType).Params);
        var otherParameter = Assert.Single(Assert.IsType<TyFun>(readOther.InferredType).Params);
        var typeRegistry = new TypeIdRegistry(result.SymbolTable, result.TypeInferer);
        Assert.NotEqual(typeRegistry.GetTypeTypeId(leafParameter), typeRegistry.GetTypeTypeId(otherParameter));
    }

    [Fact]
    public void Types_NestedClosedCaseValueGenericIdentity_RejectsDifferentIntermediateArgument()
    {
        const string source = """
Envelope[comptime N: Int] :: type {
    Branch[comptime M: Int] :: type {
        Leaf[comptime K: Int] :: type {},
    },
}

wrong :: Envelope[1].Branch[2].Leaf[3] -> Envelope[1].Branch[4].Leaf[3]
{
    value => value
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_NestedClosedCaseEffectGenericIdentity_RejectsDifferentIntermediateArgument()
    {
        const string source = """
io :: effect;
Alloc :: effect;

Envelope[E: effects] :: type {
    Branch[F: effects] :: type {
        Leaf :: type {},
    },
}

wrong :: Envelope[io].Branch[io].Leaf -> Envelope[io].Branch[Alloc].Leaf
{
    value => value
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_NestedClosedCaseGadtSpecialization_AppliesAtEveryLexicalParentEdge()
    {
        const string source = """
Envelope[T] :: type {
    Branch[A] :: type case Envelope[A] {
        Leaf[B] :: type {
            leaf :: B,
        },
    },
}

make_leaf :: Unit -> Envelope[String].Branch[String].Leaf[Bool]
{
    _ => Leaf { leaf: true }
}

as_branch :: Envelope[String].Branch[String].Leaf[Bool] -> Envelope[String].Branch[String]
{
    leaf => leaf
}

as_root :: Envelope[String].Branch[String].Leaf[Bool] -> Envelope[String]
{
    leaf => leaf
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_NestedClosedCaseValueGenericGadtSpecialization_ConstrainsLexicalParentIdentity()
    {
        const string source = """
Envelope[comptime N: Int] :: type {
    Branch[comptime M: Int] :: type case Envelope[M] {
        Leaf[comptime K: Int] :: type {},
    },
}

make_leaf :: Unit -> Envelope[2].Branch[2].Leaf[3]
{
    _ => Leaf()
}

as_root :: Envelope[2].Branch[2].Leaf[3] -> Envelope[2]
{
    value => value
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_NestedClosedCaseValueGenericGadtSpecialization_RejectsContradictoryExactPath()
    {
        const string source = """
Envelope[comptime N: Int] :: type {
    Branch[comptime M: Int] :: type case Envelope[M] {},
}

impossible :: Envelope[1].Branch[2] -> Unit
{
    _ => ()
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E4000" &&
            diagnostic.Message.Contains("specialization mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_NestedClosedCaseGadtSpecialization_MustTargetDirectLexicalParent()
    {
        const string source = """
Root[T] :: type {
    Mid :: type {
        Leaf :: type(Int) case Root[Int],
    },
}

bad :: Unit -> Root[Int].Mid.Leaf
{
    _ => Leaf(1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E4000" &&
            diagnostic.Message.Contains("GADT constructor return type", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_RecordClosedCaseGadtClause_AppearsBeforeBody()
    {
        const string source = """
Expr[T] :: type {
    IntLit :: type case Expr[Int] {
        value :: Int,
    },
}

make_int :: Unit -> Expr[Int].IntLit
{
    _ => IntLit { value: 1 }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ClosedCaseTypePath_RejectsParentArgumentsThatContradictGadtSpecialization()
    {
        const string source = """
Expr[T] :: type {
    IntLit :: type(Int) case Expr[Int],
}

impossible :: Expr[Bool].IntLit -> Bool
{
    _ => false
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E4000" &&
            diagnostic.Message.Contains("specialization mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Namer_NestedClosedCaseExhaustiveness_UsesLeavesBelowStaticIntermediateType()
    {
        const string source = """
Anim :: type {
    Mammal :: type {
        Dog :: type {},
        Cat :: type {},
    },

    Reptile :: type {
        Snake :: type {},
    },
}

classify_mammal :: Anim.Mammal -> Int
{
    Dog() => 1,
    Cat() => 2
}
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "W4200");
    }

    [Fact]
    public void Namer_NestedClosedCaseExhaustiveness_ReportsOnlyMissingLeavesBelowStaticIntermediateType()
    {
        const string source = """
Anim :: type {
    Mammal :: type {
        Dog :: type {},
        Cat :: type {},
    },

    Reptile :: type {
        Snake :: type {},
    },
}

classify_mammal :: Anim.Mammal -> Int
{
    Dog() => 1
}
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.True(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("Cat", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Snake", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Namer_NestedClosedCaseMatchExpression_UsesMatchedParameterStaticIntermediateType()
    {
        const string source = """
Anim :: type {
    Mammal :: type {
        Dog :: type {},
        Cat :: type {},
    },

    Reptile :: type {
        Snake :: type {},
    },
}

classify_mammal :: Anim.Mammal -> Int
{
    mammal => match mammal {
        Dog() => 1,
        Cat() => 2
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "W4200");
    }

    [Fact]
    public void Namer_NestedClosedCaseMatchExpression_ReportsOnlyMissingIntermediateLeaf()
    {
        const string source = """
Anim :: type {
    Mammal :: type {
        Dog :: type {},
        Cat :: type {},
    },

    Reptile :: type {
        Snake :: type {},
    },
}

classify_mammal :: Anim.Mammal -> Int
{
    mammal => match mammal {
        Dog() => 1
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.True(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("Cat", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Snake", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mir_NestedClosedCaseCommonFieldProjection_UsesTypedAncestorLayout()
    {
        const string source = """
Anim :: type {
    name :: String,

    Mammal :: type {
        warm :: Bool,

        Dog :: type {
            breed :: String,
        },
    },
}

read_anim :: Anim -> String
{
    anim => anim.name
}

read_mammal :: Anim.Mammal -> Bool
{
    mammal => mammal.warm
}

read_dog :: Anim.Mammal.Dog -> String
{
    dog => dog.breed
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.NotNull(result.MirModule);
    }

    [Fact]
    public void Borrow_ClosedCaseExactAndErasedParent_PreserveInjectionAndReferenceBoundaries()
    {
        const string source = """
Anim :: type {
    name :: String,

    Dog :: type {
        breed_code :: Int,
    },

    Cat :: type {},
}

erase :: Anim.Dog -> Anim
{
    dog => dog
}

read_exact :: Ref[Anim.Dog] -> Int
{
    dog => (*dog).breed_code
}

read_parent :: Ref[Anim] -> String
{
    anim => (*anim).name
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.NotNull(result.BorrowCheckResult);
        Assert.True(result.TypeInferer!.GetClosedCaseInjectionSnapshot().Count > 0);
    }

    [Fact]
    public void Namer_ClosedCaseReprC_DoesNotPretendSealedSumHasCStructLayout()
    {
        const string source = """
Anim :: type repr c {
    Dog :: type {},
    Cat :: type {},
}
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("sealed sum", StringComparison.OrdinalIgnoreCase));
        var anim = result.SymbolTable!.Symbols.Values.OfType<AdtSymbol>().Single(static symbol => symbol.Name == "Anim");
        Assert.False(anim.IsCStruct);
        Assert.Null(anim.CStructLayoutInfo);
    }

    [Fact]
    public void Types_ClosedCaseGraphAndReflection_RestoreFromIncrementalCacheWithoutIdentityDrift()
    {
        const string source = """
Anim :: type {
    name :: String,

    Mammal :: type {
        warm :: Bool,
        Dog :: type { breed :: String },
        Cat :: type {},
    },

    Reptile :: type {
        Snake :: type {},
    },
}

Direct :: comptime meta.cases_of(Anim);
Leaves :: comptime meta.leaf_cases_of(Anim);
DogFields :: comptime meta.fields_of(Anim.Mammal.Dog);
DogParent :: comptime meta.parent_type_of(Anim.Mammal.Dog);
DogCatJoin :: comptime meta.join_type_of(Anim.Mammal.Dog, Anim.Mammal.Cat);
""";

        var first = RunPipeline(source, CompilationPhase.Types, static options =>
        {
            options.AllowVirtualInputFile = true;
            options.LanguageVersion = EidosLanguageVersions.Current;
            options.NoImplicitPrelude = true;
            options.EnableDetailedProfiling = true;
            options.EnableIncrementalCompilation = true;
        });

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        var second = RunPipeline(source, CompilationPhase.Types, options =>
        {
            options.AllowVirtualInputFile = true;
            options.LanguageVersion = EidosLanguageVersions.Current;
            options.NoImplicitPrelude = true;
            options.EnableDetailedProfiling = true;
            options.EnableIncrementalCompilation = true;
            options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
            options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
            options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
            options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
            options.PreviousModuleTypesStatePayloads = payloads;
            options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.TypesStatePayload => payloadByModule.ContainsKey(moduleKey),
                _ => false
            };
            options.ModuleTypesStatePayloadLoader = (moduleKey, kind, _, _) =>
                kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                payloadByModule.TryGetValue(moduleKey, out var payload)
                    ? payload
                    : null;
        });

        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied"));
        AssertClosedCaseGraph(second.SymbolTable!);

        var firstValues = GetNamedComptimeValues(first);
        var secondValues = GetNamedComptimeValues(second);
        Assert.Equal(firstValues.Keys.Order(StringComparer.Ordinal), secondValues.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, firstValue) in firstValues)
        {
            Assert.True(firstValue.StructuralEquals(secondValues[name]), $"restored comptime value '{name}' changed identity");
        }

        static void AssertClosedCaseGraph(SymbolTable symbolTable)
        {
            var root = Assert.Single(symbolTable.Symbols.Values.OfType<AdtSymbol>(), static symbol =>
                !symbol.IsCaseType && string.Equals(symbol.Name, "Anim", StringComparison.Ordinal));
            Assert.Equal(["Mammal", "Reptile"], root.DirectCases.Select(id => symbolTable.GetSymbol<AdtSymbol>(id)!.Name));
            var mammal = symbolTable.GetSymbol<AdtSymbol>(root.DirectCases[0])!;
            Assert.Equal(root.Id, mammal.ParentAdt);
            Assert.Equal(["Dog", "Cat"], mammal.DirectCases.Select(id => symbolTable.GetSymbol<AdtSymbol>(id)!.Name));
            var dog = symbolTable.GetSymbol<AdtSymbol>(mammal.DirectCases[0])!;
            Assert.Equal(mammal.Id, dog.ParentAdt);
            Assert.Equal(["name"], root.Fields.Select(id => symbolTable.GetSymbol<FieldSymbol>(id)!.Name));
            Assert.Equal(["warm"], mammal.Fields.Select(id => symbolTable.GetSymbol<FieldSymbol>(id)!.Name));
            Assert.Equal(["breed"], dog.Fields.Select(id => symbolTable.GetSymbol<FieldSymbol>(id)!.Name));
        }

        static Dictionary<string, ComptimeValue> GetNamedComptimeValues(CompilationResult result)
        {
            var values = new Dictionary<string, ComptimeValue>(StringComparer.Ordinal);
            foreach (var (symbolId, value) in result.TypeInferer!.ComptimeValues)
            {
                if (result.SymbolTable!.GetSymbol<VarSymbol>(symbolId) is { IsComptime: true } symbol)
                {
                    values[symbol.Name] = value;
                }
            }
            return values;
        }
    }

    [Fact]
    public void Types_ClosedCaseEffectSpecialization_RestoresFromIncrementalCacheWithoutIdentityDrift()
    {
        const string source = """
io :: effect;
Alloc :: effect;

Envelope[E: effects] :: type {
    Branch[F: effects] :: type {
        Leaf :: type {},
        Other :: type {},
    },
}

Parent :: comptime meta.parent_type_of(Envelope[io].Branch[Alloc].Leaf);
Join :: comptime meta.join_type_of(
    Envelope[io].Branch[Alloc].Leaf,
    Envelope[io].Branch[Alloc].Other);
Subtype :: comptime meta.is_subtype(
    Envelope[io].Branch[Alloc].Leaf,
    Envelope[io].Branch[Alloc]);
""";

        var first = RunPipeline(source, CompilationPhase.Types, static options =>
        {
            options.AllowVirtualInputFile = true;
            options.LanguageVersion = EidosLanguageVersions.Current;
            options.NoImplicitPrelude = true;
            options.EnableDetailedProfiling = true;
            options.EnableIncrementalCompilation = true;
        });

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        var second = RunPipeline(source, CompilationPhase.Types, options =>
        {
            options.AllowVirtualInputFile = true;
            options.LanguageVersion = EidosLanguageVersions.Current;
            options.NoImplicitPrelude = true;
            options.EnableDetailedProfiling = true;
            options.EnableIncrementalCompilation = true;
            options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
            options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
            options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
            options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
            options.PreviousModuleTypesStatePayloads = payloads;
            options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.TypesStatePayload => payloadByModule.ContainsKey(moduleKey),
                _ => false
            };
            options.ModuleTypesStatePayloadLoader = (moduleKey, kind, _, _) =>
                kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                payloadByModule.TryGetValue(moduleKey, out var payload)
                    ? payload
                    : null;
        });

        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied"));
        var firstValues = GetNamedComptimeValues(first);
        var secondValues = GetNamedComptimeValues(second);
        foreach (var name in new[] { "Parent", "Join", "Subtype" })
        {
            Assert.True(firstValues[name].StructuralEquals(secondValues[name]), $"restored comptime value '{name}' changed identity");
        }

        var parent = Assert.IsType<ComptimeTypeValue>(secondValues["Parent"]);
        Assert.Equal(["io", "Alloc"], parent.TypeRef.GenericArguments!.Select(static argument => argument.Display));
        Assert.True(Assert.IsType<ComptimeBoolValue>(secondValues["Subtype"]).Value);

        static Dictionary<string, ComptimeValue> GetNamedComptimeValues(CompilationResult result)
        {
            var values = new Dictionary<string, ComptimeValue>(StringComparer.Ordinal);
            foreach (var (symbolId, value) in result.TypeInferer!.ComptimeValues)
            {
                if (result.SymbolTable!.GetSymbol<VarSymbol>(symbolId) is { IsComptime: true } symbol)
                {
                    values[symbol.Name] = value;
                }
            }
            return values;
        }
    }
}
