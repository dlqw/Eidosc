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
    public void Types_HigherKindedTypeParam_UnifiesWithConcreteAdt_Succeeds()
    {
        const string source = """
Box[T] :: type {
    Wrap:: type(T)
}

hkId[F: kind2, A] :: F[A] -> F[A]
{
    fa => fa
}

use :: Box[Int] -> Box[Int]
{
    x => hkId(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_HigherKindedTypeParam_MissingRequiredTypeArgument_Fails()
    {
        const string source = """
bad[F: kind2] :: F -> Int
{
    x => 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type parameter 'F' expects 1 type argument(s), but got 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_HigherKindedTypeParam_TypeArgumentArityMismatch_Fails()
    {
        const string source = """
bad[F: kind2] :: F[Int, String] -> Int
{
    x => 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Kind 'kind1' cannot be applied to additional type arguments", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_HigherKindedTypeParam_TwoArityConstructor_Succeeds()
    {
        const string source = """
Pair[A, B] :: type {
    Pair:: type(A, B)
}

hk2Id[F: kind3, A, B] :: F[A, B] -> F[A, B]
{
    value => value
}

use :: Pair[Int, Bool] -> Pair[Int, Bool]
{
    p => hk2Id(p)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_HigherKindedTypeParam_ComposedTypeConstructorApplication_Succeeds()
    {
        const string source = """
ho[F: kind2, G: kind2, A] :: F[G[A]] -> F[G[A]]
{
    x => x
}

use[H: kind2, K: kind2, A] :: H[K[A]] -> H[K[A]]
{
    x => ho(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_HigherKindedTypeParam_ConstructorKindMismatchInArgument_Fails()
    {
        const string source = """
bad[F: kind2, G: kind2] :: F[G] -> F[G]
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Kind mismatch in type application", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_HigherKindedTypeParam_UnannotatedUnary_InferredFromApplication_Succeeds()
    {
        const string source = """
Box[A] :: type {
    Wrap:: type(A)
}

hkId[F, A] :: F[A] -> F[A]
{
    x => x
}

use :: Box[Int] -> Box[Int]
{
    x => hkId(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_HigherKindedTypeParam_UnannotatedHigherOrder_InferredFromComposition_Succeeds()
    {
        const string source = """
ho[F, G, A] :: F[G[A]] -> F[G[A]]
{
    x => x
}

use[H: kind2, K: kind2, A] :: H[K[A]] -> H[K[A]]
{
    x => ho(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_HigherKindedTypeParam_UnannotatedConflictingArity_Fails()
    {
        const string source = """
bad[F, A] :: F[A] -> F[Int, String] -> Int
{
    x => y => 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains(
                              "Kind 'kind1' cannot be applied to additional type arguments",
                              StringComparison.Ordinal));
    }

    [Fact]
    public void Types_HigherKindedTypeParam_ParenthesizedHigherOrderKind_Succeeds()
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

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_TraitMethod_MethodLevelHigherKindedTypeParam_Succeeds()
    {
        const string source = """
Traversable[T: kind2] :: trait {
    traverse[A, B, G: kind2] :: (A -> G[B]) -> T[A] -> G[T[B]]
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Kind 'kind1' cannot be applied to additional type arguments", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraitMethod_MethodLevelHigherKindedTypeParamWithTraitConstraint_Succeeds()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Traversable[T: kind2] :: trait {
    traverse[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> T[A] -> G[T[B]]
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Kind 'kind1' cannot be applied to additional type arguments", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraversableCall_ReverseInfersPartiallyAppliedResultConstructor_FromConcreteReturnType()
    {
        const string source = """
Applicative[F: kind2] :: trait {
}

Result[T, E] :: type {
    Ok:: type(T) , Err:: type(E)
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

positive_result :: Int -> Result[Int, String]
{
    x => if x > 0 then { Ok(x + 1) } else { Err("bad") }
}

main :: Unit -> Int
{
    _ => {
        produced := use(positive_result)(2);
        match produced
        {
            Ok(inner) => inner,
            Err(_) => 0
        }
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type argument count mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraitConstrainedCall_ReverseInfersDirectPartiallyAppliedNamedConstructor_FromConcreteReturnType()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Either[A, B] :: type {
    Left:: type(A) , Right:: type(B)
}


ApplicativeEitherString :: instance Applicative[Either[String]] {
    pure[A] :: A -> Either[String, A] {
        value => Right(value)
    }
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

positive_either :: Int -> Either[String, Int]
{
    x => if x > 0 then { Right(x + 1) } else { Left("bad") }
}

main :: Unit -> Int
{
    _ => match use(positive_either)(2)
    {
        Right(v) => v,
        Left(_) => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type argument count mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Applicative", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraitConstrainedCall_ReverseInfersDeepAliasChainPartiallyAppliedConstructor_FromConcreteReturnType()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok:: type(T) , Err:: type(E)
}

ResultWith[E, T] :: type = Result[T, E];
BoxedResult[E, T] :: type = ResultWith[E, T];
DeepBoxedResult[E, T] :: type = BoxedResult[E, T];


ApplicativeDeepBoxedResultString :: instance Applicative[DeepBoxedResult[String]] {
    pure[A] :: A -> DeepBoxedResult[String, A] {
        value => Ok(value)
    }
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

positive_result :: Int -> DeepBoxedResult[String, Int]
{
    x => if x > 0 then { Ok(x + 1) } else { Err("bad") }
}

main :: Unit -> Int
{
    _ => match use(positive_result)(2)
    {
        Ok(v) => v,
        Err(_) => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type argument count mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Applicative", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraitConstrainedCall_ReverseInfersMiddleOpenAliasConstructor_FromConcreteReturnType()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Triple[A, B, C] :: type {
    Triple:: type(A, B, C)
}

KeepEdges[L, R, X] :: type = Triple[L, X, R];


ApplicativeKeepEdgesStringBool :: instance Applicative[KeepEdges[String, Bool]] {
    pure[A] :: A -> KeepEdges[String, Bool, A] {
        value => Triple("ctx", value, true)
    }
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

produce :: Int -> KeepEdges[String, Bool, Int]
{
    x => Triple("ctx", x + 1, true)
}

main :: Unit -> Int
{
    _ => match use(produce)(2)
    {
        Triple(_, value, true) => value,
        Triple(_, _, false) => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type argument count mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Applicative", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraitConstrainedCall_ReverseInfersDeepAliasImplFromUnderlyingConstructorReturnType()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok:: type(T) , Err:: type(E)
}

ResultWith[E, T] :: type = Result[T, E];
BoxedResult[E, T] :: type = ResultWith[E, T];
DeepBoxedResult[E, T] :: type = BoxedResult[E, T];


ApplicativeDeepBoxedResultString :: instance Applicative[DeepBoxedResult[String]] {
    pure[A] :: A -> DeepBoxedResult[String, A] {
        value => Ok(value)
    }
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

positive_result :: Int -> Result[Int, String]
{
    x => if x > 0 then { Ok(x + 1) } else { Err("bad") }
}

main :: Unit -> Int
{
    _ => match use(positive_result)(2)
    {
        Ok(v) => v,
        Err(_) => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Applicative", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraitConstrainedCall_ReverseInfersMiddleOpenAliasImplFromUnderlyingConstructorReturnType()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Triple[A, B, C] :: type {
    Triple:: type(A, B, C)
}

KeepEdges[L, R, X] :: type = Triple[L, X, R];


ApplicativeKeepEdgesStringBool :: instance Applicative[KeepEdges[String, Bool]] {
    pure[A] :: A -> KeepEdges[String, Bool, A] {
        value => Triple("ctx", value, true)
    }
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

produce :: Int -> Triple[String, Int, Bool]
{
    x => Triple("ctx", x + 1, true)
}

main :: Unit -> Int
{
    _ => match use(produce)(2)
    {
        Triple(_, value, true) => value,
        Triple(_, _, false) => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Applicative", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_TraitConstrainedCall_MultipleAliasImplCandidatesForSameUnderlyingConstructor_ReportsAmbiguousImplDiagnostic()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok:: type(T) , Err:: type(E)
}

DeepBoxedResult[E, T] :: type = Result[T, E];
AlsoResult[E, T] :: type = Result[T, E];


ApplicativeDeepBoxedResultString :: instance Applicative[DeepBoxedResult[String]] {
    pure[A] :: A -> DeepBoxedResult[String, A] {
        value => Ok(value)
    }
}


ApplicativeAlsoResultString :: instance Applicative[AlsoResult[String]] {
    pure[A] :: A -> AlsoResult[String, A] {
        value => Ok(value)
    }
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

positive_result :: Int -> Result[Int, String]
{
    x => if x > 0 then { Ok(x + 1) } else { Err("bad") }
}

main :: Unit -> Int
{
    _ => match use(positive_result)(2)
    {
        Ok(v) => v,
        Err(_) => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E3004" &&
                    item.Message.Contains("Ambiguous overlapping instance registration", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requested instance head: instance Applicative[AlsoResult[String]]", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("existing instance head: instance Applicative[DeepBoxedResult[String]]", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("canonical head", StringComparison.Ordinal) &&
                                                  note.Contains("Applicative", StringComparison.Ordinal));
        Assert.Single(diagnostic.Related);
    }

    [Fact]
    public void Types_DirectTraitMethodCall_MultipleAliasImplCandidatesForSameUnderlyingConstructor_ReportsAmbiguousImplDiagnostic()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok:: type(T) , Err:: type(E)
}

DeepBoxedResult[E, T] :: type = Result[T, E];
AlsoResult[E, T] :: type = Result[T, E];


ApplicativeDeepBoxedResultString :: instance Applicative[DeepBoxedResult[String]] {
    pure[A] :: A -> DeepBoxedResult[String, A] {
        value => Ok(value)
    }
}


ApplicativeAlsoResultString :: instance Applicative[AlsoResult[String]] {
    pure[A] :: A -> AlsoResult[String, A] {
        value => Ok(value)
    }
}

make :: Unit -> Result[Int, String]
{
    _ => pure(1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E3004" &&
                    item.Message.Contains("Ambiguous overlapping instance registration", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requested instance head: instance Applicative[AlsoResult[String]]", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("existing instance head: instance Applicative[DeepBoxedResult[String]]", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("canonical head", StringComparison.Ordinal) &&
                                                  note.Contains("Applicative", StringComparison.Ordinal));
        Assert.Single(diagnostic.Related);
    }

    [Fact]
    public void Types_DirectTraitMethodCall_WithGenericAndSpecializedImpls_Succeeds()
    {
        const string source = """
Show :: trait {
    show :: Self -> Int
}

Option[T] :: type {
    Some:: type(T) , None :: type {}
}


ShowOption[T] :: instance Show {
    show :: Option[T] -> Int {
        _ => 0
    }
}


ShowOptionInt :: instance Show {
    show :: Option[Int] -> Int {
        _ => 1
    }
}

render :: Option[Int] -> Int
{
    value => show(value)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }
}
