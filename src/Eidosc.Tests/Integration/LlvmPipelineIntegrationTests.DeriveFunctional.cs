using Xunit;

namespace Eidosc.Tests.Integration;

public sealed partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void DerivedFunctor_NativeSmoke_ReturnsMappedValue()
    {
        const string source = """
import std.Functor

@[derive(Functor)]
Box[A] :: type { Box :: type(A) }

increment :: Int -> Int { value => value + 1 }

main :: Unit -> Int
{
    mapped := Functor.fmap(Box(41))(increment);
    match mapped { Box(value) => value }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "derive_functor_native.eidos",
            "derive_functor_native");

        Assert.Equal(42, execution.ExitCode);
    }

    [Fact]
    public void DerivedFoldable_NativeSmoke_FoldsFieldsInSourceOrder()
    {
        const string source = """
import std.Foldable

@[derive(Foldable)]
Pair[A] :: type { Pair :: type(A, A) }

append_digit :: Int -> Int -> Int { total => value => total * 10 + value }

main :: Unit -> Int
{
    Foldable.fold_left(Pair(4, 2))(0)(append_digit)
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "derive_foldable_native.eidos",
            "derive_foldable_native");

        Assert.Equal(42, execution.ExitCode);
    }

    [Fact]
    public void DerivedTraversable_NativeSmoke_MapsSingleField()
    {
        const string source = """
import std.Option
import std.Traversable

@[derive(Traversable)]
Box[A] :: type { Box :: type(A) }

increment_some :: Int -> Option[Int] { value => Some(value + 1) }

main :: Unit -> Int
{
    traversed := Traversable.traverse(Box(41))(increment_some);
    match traversed {
        Some(Box(value)) => value,
None() => 0
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "derive_traversable_single_native.eidos",
            "derive_traversable_single_native");

        Assert.Equal(42, execution.ExitCode);
    }

    [Fact]
    public void DerivedTraversable_NativeSmoke_SequencesApplicativeFields()
    {
        const string source = """
import std.Option
import std.Traversable

@[derive(Traversable)]
Pair[A] :: type { Pair :: type(A, A) }

increment_some :: Int -> Option[Int] { value => Some(value + 1) }

main :: Unit -> Int
{
    traversed := Traversable.traverse(Pair(20, 20))(increment_some);
    match traversed {
        Some(Pair(left, right)) => left + right,
        None() => 0
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "derive_traversable_native.eidos",
            "derive_traversable_native");

        Assert.Equal(42, execution.ExitCode);
    }

    [Fact]
    public void TraversableMap2_NativeSmoke_AppliesCurriedConstructor()
    {
        const string source = """
import std.Option
import std.Traversable

Pair[A] :: type { Pair :: type(A, A) }

make_pair[A] :: A -> A -> Pair[A] { left => right => Pair(left, right) }

main :: Unit -> Int
{
    result := Some(20).map(make_pair).apply(Some(20));
    match result { Some(Pair(left, right)) => left + right, None() => 0 }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "traversable_map2_constructor_native.eidos",
            "traversable_map2_constructor_native");

        Assert.Equal(40, execution.ExitCode);
    }

    [Fact]
    public void DerivedTraversable_NativeSmoke_SequencesThreeApplicativeFields()
    {
        const string source = """
import std.Option
import std.Traversable

@[derive(Traversable)]
Triple[A] :: type { Triple :: type(A, A, A) }

increment_some :: Int -> Option[Int] { value => Some(value + 1) }

main :: Unit -> Int
{
    traversed := Traversable.traverse(Triple(12, 13, 14))(increment_some);
    match traversed {
        Some(Triple(first, second, third)) => first + second + third,
        None() => 0
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "derive_traversable_triple_native.eidos",
            "derive_traversable_triple_native");

        Assert.Equal(42, execution.ExitCode);
    }
}
