using System;
using System.IO;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_ContextualRecordLiteral_UsesTypedBindingExpectedType()
    {
        const string source = """
Pos :: type { x: Int, y: Int }

origin :: Pos = .{ x: 0, y: 0 };
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ContextualRecordLiteral_UsesFunctionReturnExpectedType()
    {
        const string source = """
Pos :: type { x: Int, y: Int }

make_pos :: Int -> Int -> Pos
{
    x => y => .{ x: x, y: y }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ContextualRecordLiteral_WithoutExpectedTypeReportsDiagnostic()
    {
        const string source = """
Pos :: type { x: Int, y: Int }

make_pos :: Unit -> Pos
{
    _ => {
        p := .{ x: 0, y: 0 };
        p
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot infer the type of contextual record literal", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_RecordPatternRest_BindsOnlyListedFields()
    {
        const string source = """
GameState :: type {
    dir: Int,
    score: Int,
    tick: Int
}

read_dir :: GameState -> Int
{
    state => match state {
        GameState { dir: d, .. } => d
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_GadtConstructor_ReturnsDeclaredResultType()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int] ,
    BoolLit(Bool) -> Expr[Bool]
}

make_int :: Unit -> Expr[Int]
{
    _ => IntLit(1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_GadtConstructor_WrongExpectedResultTypeFails()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int] ,
    BoolLit(Bool) -> Expr[Bool]
}

bad :: Unit -> Expr[Bool]
{
    _ => IntLit(1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Bool", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GadtPattern_RefinesBranchResult()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int]
}

eval_int :: Expr[Int] -> Int
{
    expr => match expr
    {
        IntLit(value) => value
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_GadtPattern_MultipleBranchesKeepRefinementsLocal()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int] ,
    BoolLit(Bool) -> Expr[Bool]
}

classify[T] :: Expr[T] -> Int
{
    expr => match expr
    {
        IntLit(value) => value,
        BoolLit(flag) => if flag then { 1 } else { 0 }
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Cannot unify type", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GadtPattern_BranchesCanReturnRefinedScrutineeType()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int] ,
    BoolLit(Bool) -> Expr[Bool]
}

eval[T] :: Expr[T] -> T
{
    expr => match expr
    {
        IntLit(value) => value,
        BoolLit(flag) => flag
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_GadtPattern_FunctionBodyBranchesKeepRefinementsLocal()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int] ,
    BoolLit(Bool) -> Expr[Bool]
}

eval[T] :: Expr[T] -> T
{
    IntLit(value) => value,
    BoolLit(flag) => flag
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_GadtPattern_IfLetThenBranchGetsLocalEqualityEvidence()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int]
}

proof_in_iflet[T] :: Expr[T] -> Int
{
    expr => if let IntLit(_) = expr then {
        p: TypeEq[T, Int] := Refl;
        1
    } else {
        0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_GadtPattern_WhileLetBodyGetsLocalEqualityEvidence()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int]
}

proof_in_whilelet[T] :: Expr[T] -> Unit
{
    expr => {
        while let IntLit(_) = expr then {
            p: TypeEq[T, Int] := Refl;
            ()
        };
        ()
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_TypeEq_Refl_LowersThroughMir()
    {
        const string source = """
proof_ok :: Unit -> TypeEq[Int, Int]
{
    _ => Refl
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
    }

    [Fact]
    public void Types_TypeEq_Refl_ProvesOnlyIdenticalTypes()
    {
        const string source = """
ok :: Unit -> TypeEq[Int, Int]
{
    _ => Refl
}

bad :: Unit -> TypeEq[Int, Bool]
{
    _ => Refl
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TypeEq_Refl_DoesNotInventGenericEquality()
    {
        const string source = """
bad[T] :: Unit -> TypeEq[T, Int]
{
    _ => Refl
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Refl", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("TypeEq", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GadtPattern_LocalEqualityEvidence_ProvesBranchTypeEq()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int]
}

proof_in_branch[T] :: Expr[T] -> TypeEq[T, Int]
{
    expr => match expr
    {
        IntLit(_) => {
            p: TypeEq[T, Int] := Refl;
            p
        }
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_GadtPattern_LocalEqualityEvidence_DoesNotLeakOutsideBranch()
    {
        const string source = """
Expr[T] :: type {
    IntLit(Int) -> Expr[Int] ,
    BoolLit(Bool) -> Expr[Bool]
}

proof_outside[T] :: Expr[T] -> TypeEq[T, Int]
{
    expr => {
        _ := match expr
        {
            IntLit(_) => (),
            BoolLit(_) => ()
        };
        Refl
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Refl", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("TypeEq", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GadtConstructor_ReturningOtherAdtFails()
    {
        const string source = """
Other[T] :: type { Other(T) }

Expr[T] :: type {
    Bad(Int) -> Other[Int]
}

use :: Unit -> Expr[Int]
{
    _ => Bad(1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("GADT constructor return type", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GadtConstructor_ReturnTypeWrongTypeArgumentCountFails()
    {
        const string source = """
Expr[T] :: type {
    Bad(Int) -> Expr
}

use :: Unit -> Expr[Int]
{
    _ => Bad(1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("argument", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GadtConstructor_ReturnTypeKindMismatchFails()
    {
        const string source = """
Expr[F: kind2] :: type {
    Bad(Int) -> Expr[Int]
}

use :: Unit -> Expr[List]
{
    _ => Bad(1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("kind", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Types_ExistentialConstructorLocalParam_ConstructsDifferentPayloadTypes()
    {
        const string source = """
Box :: type {
    Pack[A](A) -> Box
}

make_int :: Unit -> Box
{
    _ => Pack(1)
}

make_string :: Unit -> Box
{
    _ => Pack("x")
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ExistentialConstructorLocalParam_CanUnpackInsidePatternBranch()
    {
        const string source = """
Box :: type {
    Pack[A](A) -> Box
}

consume :: Box -> Int
{
    Pack(_) => 1
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ExistentialConstructorLocalParam_DoesNotLeakConcreteType()
    {
        const string source = """
Direction[A] :: type {
    North -> Direction[Int] ,
    East -> Direction[Bool]
}

AnyDirection :: type {
    AnyDirection[A](Direction[A]) -> AnyDirection
}

bad :: AnyDirection -> Direction[Int]
{
    AnyDirection(dir) => dir
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Cannot unify", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ExistentialGadtWrapper_AllowsDerivedTraitDispatchInsideBranch()
    {
        const string source = """
import Std.Trait

Axis :: type {
    Horizontal , Vertical
}

DirectionVector :: trait {
    dx :: Self -> Int
    dy :: Self -> Int
}

Direction[A] :: type {
    North -> Direction[Vertical] ,
    East -> Direction[Horizontal]
}

DirectionVectorDirection[A] :: instance DirectionVector for Direction[A] {
    North => { dx = 0, dy = -1 } |
    East => { dx = 1, dy = 0 }
}

AnyDirection :: type {
    AnyDirection[A](Direction[A]) -> AnyDirection
}

dx_any :: AnyDirection -> Int
{
    AnyDirection(dir) => dir.dx()
}
""";

        var result = RunPipelineWithTemporaryInput(source, CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
    }

    [Fact]
    public void Types_ExistentialGadtWrapper_AllowsNestedPayloadConstructorPatterns()
    {
        const string source = """
import Std.Trait

Axis :: type {
    Horizontal , Vertical
}

@derive(Clone)
Direction[A] :: type {
    North -> Direction[Vertical] ,
    South -> Direction[Vertical] ,
    East -> Direction[Horizontal] ,
    West -> Direction[Horizontal]
}

AnyDirection :: type {
    AnyDirection[A](Direction[A]) -> AnyDirection
}

opposite :: AnyDirection -> AnyDirection
{
    AnyDirection(North()) => AnyDirection(South()),
    AnyDirection(South()) => AnyDirection(North()),
    AnyDirection(East()) => AnyDirection(West()),
    AnyDirection(West()) => AnyDirection(East())
}
""";

        var result = RunPipelineWithTemporaryInput(source, CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
    }

    [Fact]
    public void Types_ConstructorLocalConstraint_AppliesInsidePatternBranch()
    {
        const string source = """
Label :: trait {
    label :: Self -> Int
}

Token :: type {
    Token
}

@impl(Label)
label :: Token -> Int
{
    _ => 7
}

Box :: type {
    Pack[A: Label](A) -> Box
}

label_box :: Box -> Int
{
    Pack(x) => x.label()
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_RecordUpdateSpread_FillsUnspecifiedFields()
    {
        const string source = """
GameState :: type {
    GameState {
        snake: Int,
        dir: Int,
        tick: Int
    }
}

reset_tick :: GameState -> GameState
{
    state => { GameState { ..state, tick: 0 } }
}

read_state :: GameState -> Int
{
    state => {
        updated := reset_tick(state);
        updated.snake + updated.dir + updated.tick
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_RecordUpdateShorthand_FillsUnspecifiedFields()
    {
        const string source = """
GameState :: type {
    GameState {
        snake: Int,
        dir: Int,
        tick: Int
    }
}

reset_tick :: GameState -> GameState
{
    state => { state.{ tick: 0 } }
}

read_state :: GameState -> Int
{
    state => {
        updated := reset_tick(state);
        updated.snake + updated.dir + updated.tick
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_RecordUpdateShorthand_MultiConstructorCommonField_PreservesVariant()
    {
        const string source = """
Shape :: type {
    Circle {
        radius: Int,
        color: Int
    }
    , Rect {
        width: Int,
        height: Int,
        color: Int
    }
}

repaint :: Shape -> Shape
{
    shape => { shape.{ color: 1 } }
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_RecordUpdateShorthand_MultiConstructorNonCommonField_ReportsError()
    {
        const string source = """
Shape :: type {
    Circle {
        radius: Int,
        color: Int
    }
    , Rect {
        width: Int,
        height: Int,
        color: Int
    }
}

resize :: Shape -> Shape
{
    shape => { shape.{ radius: 2 } }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Record update field 'radius' is not present on every constructor", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_RecordUpdateShorthand_GenericMultiConstructorCommonField_Succeeds()
    {
        const string source = """
Slot[T] :: type {
    One {
        value: T,
        mark: Int
    }
    , Two {
        left: T,
        right: T,
        mark: Int
    }
}

mark[T] :: Slot[T] -> Slot[T]
{
    slot => { slot.{ mark: 1 } }
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_AdtTypeArgument_HigherKindedConstructor_Succeeds()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

Lift[F: kind2] :: type {
    Lift(F[Int])
}

use :: Lift[Box] -> Lift[Box]
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_AdtTypeArgument_HigherKindedMismatch_Fails()
    {
        const string source = """
Lift[F: kind2] :: type {
    Lift(F[Int])
}

bad :: Lift[Int] -> Lift[Int]
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains(
                              "Kind mismatch for type argument #1 ('F') of 'Lift'",
                              StringComparison.Ordinal));
    }

    [Fact]
    public void Types_AdtTypeArgument_PartiallyAppliedNamedTypeConstructor_Succeeds()
    {
        const string source = """
Either[A, B] :: type {
    Left(A) , Right(B)
}

Lift[F: kind2] :: type {
    Lift(F[Int])
}

use :: Lift[Either[String]] -> Lift[Either[String]]
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_AdtTypeArgument_PartiallyAppliedNamedTypeConstructor_TooManyArgs_Fails()
    {
        const string source = """
Either[A, B] :: type {
    Left(A) , Right(B)
}

Lift[F: kind2] :: type {
    Lift(F[Int])
}

bad :: Lift[Either[String, Int, Bool]] -> Lift[Either[String, Int, Bool]]
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains(
                              "Type 'Either' expects 2 type argument(s), but got 3",
                              StringComparison.Ordinal));
    }

    [Fact]
    public void Types_AdtTypeParam_UnannotatedUnaryKind_InferredFromConstructorUsage_Succeeds()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

Lift[F] :: type {
    Lift(F[Int])
}

use :: Lift[Box] -> Lift[Box]
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_AdtTypeParam_UnannotatedHigherOrderKind_InferredFromConstructorUsage_Succeeds()
    {
        const string source = """
Box[A] :: type {
    Wrap(A)
}

ApplyToInt[F: kind2] :: type {
    ApplyToInt(F[Int])
}

UseK[K] :: type {
    UseK(K[Box])
}

use :: UseK[ApplyToInt] -> UseK[ApplyToInt]
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_AdtTypeParam_UnannotatedConflictingKindUsage_Fails()
    {
        const string source = """
Bad[F] :: type {
    A(F[Int]) ,
    B(F)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                              diagnostic.Message.Contains("Kind mismatch in ADT 'Bad' definition", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ListComprehension_MultipleGeneratorsAndGuard_Succeeds()
    {
        const string source = """
listA :: [1, 2];
listB :: [10, 20];
xs :: [a + b | a <- listA, b <- listB, a + b > 15];
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_ListComprehension_NonBoolGuard_Fails()
    {
        const string source = """
xs :: [x | x <- [1, 2], x + 1];
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Seq comprehension guard must be Bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ListPattern_WithRestBinding_Succeeds()
    {
        const string source = """
head_or_zero :: Int -> Int
{
    _ => match [1, 2, 3]
    {
        [head, ..tail] => head,
        [] => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_ListPattern_ScrutineeNotList_Fails()
    {
        const string source = """
bad :: Int -> Int
{
    x => match x
    {
        [a] => a,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Seq", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_RangePattern_ReversedIntBounds_Fails()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        5..3 => 1,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E4011" &&
                    item.Message.Contains("Range pattern start must be less than or equal to end", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("range start boundary", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("range end boundary", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("start <= end", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_RangePattern_ReversedCharBounds_Fails()
    {
        const string source = """
classify :: Char -> Int
{
    x => match x
    {
        'z'..'a' => 1,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E4011" &&
                    item.Message.Contains("Range pattern start must be less than or equal to end", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("range start boundary", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("range end boundary", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("start <= end", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_RangePattern_IncompatibleScrutineeType_ReportsBoundaryLabels()
    {
        const string source = """
classify :: String -> Int
{
    x => match x
    {
        1..3 => 1,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E4012" &&
                    item.Message.Contains("Range pattern expects Int or Char scrutinee, got String", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("range start boundary", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("range end boundary", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("Scrutinee type inferred as: String", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_AsPattern_MismatchedScrutineeType_Fails()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        ("a" as s) => 1,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4013" &&
                          diagnostic.Message.Contains("As-pattern inner type mismatch", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("as-pattern binding", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("as-pattern inner pattern", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("Scrutinee type inferred as: Int", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("match the scrutinee type", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_AsPattern_MatchingScrutineeType_Succeeds()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 as n) => n,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Cannot unify type 'Int' with 'String'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4013");
    }

    // A bare product type `T :: type { a: A, b: B }` desugars to a default constructor
    // named after the type, equivalent to `T :: type { T { a: A, b: B } }`.

    [Fact]
    public void Types_BareProductType_ConstructsWithNamedFields()
    {
        const string source = """
GameState :: type {
    snake: Int,
    dir: Int,
    tick: Int
}

init :: Unit -> GameState
{
    _ => GameState { snake: 0, dir: 1, tick: 0 }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_BareProductType_SupportsRecordUpdateSpread()
    {
        const string source = """
GameState :: type {
    snake: Int,
    dir: Int,
    tick: Int
}

reset_tick :: GameState -> GameState
{
    state => { GameState { ..state, tick: 0 } }
}

read_state :: GameState -> Int
{
    state => {
        updated := reset_tick(state);
        updated.snake + updated.dir + updated.tick
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_BareProductType_SupportsRecordPattern()
    {
        const string source = """
GameState :: type {
    snake: Int,
    dir: Int,
    tick: Int
}

read_dir :: GameState -> Int
{
    GameState { snake: _, dir: d, tick: _ } => d
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_BareProductType_FieldAccess()
    {
        const string source = """
Point :: type {
    x: Int,
    y: Int
}

make :: Int -> Int -> Point
{
    x => y => Point { x: x, y: y }
}

sum_x :: Point -> Int
{
    p => p.x + p.y
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }
}
