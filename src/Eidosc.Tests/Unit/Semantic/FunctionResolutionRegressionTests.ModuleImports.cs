using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ModuleQualifiedStdlibCalls_StayDisambiguatedAcrossSameNamedExports()
    {
        const string source = """
import std.Option
import std.Result
import std.Seq
import std.Text

clone_text :: String -> String
{
    value => Text.clone(ref value)
}

inc :: Int -> Int
{
    x => x + 1
}

main :: Unit -> Int
{
    _ => {
        xs := [1, 2, 3];
        resultBase: Result[Int, String] := Ok(1);

        viaOption := Option.unwrap_or(Option.map(Some(1))(inc))(0);
        viaResult := Result.unwrap_or(Result.map(resultBase)(inc))(0);
        viaList := Seq.len(Seq.map(xs)(inc));
        viaText := Text.len(clone_text("ab"));

        viaOption + viaResult + viaList + viaText
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Borrow,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ModuleImport_ExposesImportedTraitMethodsAsBareNames()
    {
        const string source = """
import std.Traits
import std.Text

clone_text :: String -> String
{
    value => Text.clone(ref value)
}

render[T: Traits.Show] :: T -> String
{
    value => show(value)
}

main :: Unit -> String
{
    _ => render(clone_text("ok"))
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined identifier 'show'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CurrentLowercaseModuleQualifier_WinsOverSameNamedImportedTraitMethod()
    {
        const string source = """
hash :: module {
    import std.Traits

    string_with_seed :: String -> Int -> Int
    {
        _ => seed => seed
    }

    hash_string :: String -> Int
    {
        value => hash.string_with_seed(value)(17)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ModuleImport_ExposesPublicValuesAsBareNames()
    {
        const string source = """
import std.Seq

main :: Unit -> Int
{
    _ => {
        xs := append([1, 2])([3]);
        len(reverse(xs))
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined identifier", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ModuleImport_ExposesImportedInstanceHelpersAsBareNames()
    {
        const string source = """
import std.Result

recover_err :: String -> Result[Int, String]
{
    _ => Ok(5)
}

main :: Unit -> Int
{
    _ => unwrap_or(or_else(Err("oops"))(recover_err))(0)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined function", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_UnqualifiedStdFunctionCall_UsesFirstArgumentTypeToInferModule()
    {
        const string source = """
import std.Seq
import std.Option

main :: Unit -> Int
{
    _ => {
        _ := append([1, 2])([3]);
        0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined function 'append'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_UnqualifiedSameNamedStdFunctionCall_SelectsOptionByArgumentType()
    {
        const string source = """
main :: Unit -> Int
{
    _ => unwrap_or(Some(41))(0) + unwrap_or(Ok(1))(0)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined function 'unwrap_or'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_RepeatedUnqualifiedStdCallableLookup_UsesCandidateCache()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        first := append([1])([2]);
        second := append([3])([4]);
        std.Seq.len(first) + std.Seq.len(second)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Types.callableCandidateCache.misses", out var misses),
            FormatCounters(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Types.callableCandidateCache.hits", out var hits),
            FormatCounters(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Types.callableResolutionCache.hits", out var resolutionHits),
            FormatCounters(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Types.callableResolutionCache.misses", out var resolutionMisses),
            FormatCounters(result));
        Assert.True(misses > 0, FormatCounters(result));
        Assert.True(hits > 0, FormatCounters(result));
        Assert.True(resolutionMisses > 0, FormatCounters(result));
        Assert.True(resolutionHits > 0, FormatCounters(result));
        Assert.NotNull(result.TypeDirectedCallableResolutionSnapshot);
        Assert.True(result.TypeDirectedCallableResolutionSnapshot!.Entries.Count > 0, FormatCounters(result));

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            EnableDetailedProfiling = true,
            PreviousTypeDirectedCallableResolutionSnapshot = result.TypeDirectedCallableResolutionSnapshot,
            UseColors = false
        }).Run();

        Assert.True(
            second.Success,
            string.Join(Environment.NewLine, second.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.True(
            second.ProfilingCounters.TryGetValue("Types.callableResolutionPreviousCache.hits", out var previousHits),
            FormatCounters(second));
        Assert.True(previousHits > 0, FormatCounters(second));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Types.callableResolutionPreviousCache.restoreHits") > 0,
            FormatCounters(second));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Types.callableResolutionPreviousCache.validatedHits") > 0,
            FormatCounters(second));
        Assert.NotNull(CompilationProfilingFormatter.CreateSnapshot(second).TypeDirectedCallableResolution);
    }

    [Fact]
    public void CompilationPipeline_BareImportedCallUsesArgumentTypesForSameNamedModuleMembers()
    {
        const string source = """
import std.Seq
import std.Option

main :: Unit -> Int
{
    _ => {
        xs := append([1])([2]);
        Seq.len(xs)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous imported value 'append'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BareImportedCallCanSelectOptionOverSameNamedListMember()
    {
        const string source = """
import std.Seq
import std.Option

main :: Unit -> Int
{
    _ => {
        value := append(Some(1))(Some(2));
        Option.unwrap_or(value)(0)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("No imported overload of 'append'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous imported value 'append'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BareImportedTraitHelpersAcrossModules_ResolveByArgumentTypes()
    {
        const string source = """
import std.Option
import std.Result
import std.Ordering
import std.Seq

add :: Int -> Int -> Int
{
    left => right => left + right
}

positive_opt :: Int -> Option[Int]
{
    x => if x > 0 then { Some(x + 1) } else { None() }
}

main :: Unit -> Int
{
    _ => {
        optionApplied := unwrap_or(apply(Some(add(20)))(Some(3)))(0);
        optionShown := show(Some(8));
        resultShown := show(Ok(7));
        orderShown := Ordering.show(compare_int(1)(2));
        optionCompare := if is_lt(compare(None())(Some(1))) then { 1 } else { 0 };
        resultInput: Result.With[String, Int] := Ok(2);
        resultTraversed := match traverse(resultInput)(x => Ok(x + 1))
        {
            Ok(inner) => unwrap_or(inner)(0),
            Err(_) => 0
        };
        listTraversed := match traverse([1, 2])(positive_opt)
        {
            Some(values) => unwrap_or(head(values))(0),
            None() => 0
        };

        if optionShown == "Some(8)" &&
           resultShown == "Ok(7)" &&
           orderShown == "Less" then {
            optionApplied + optionCompare + resultTraversed + listTraversed
        }
        else { 0 }
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("42_stdlib_safe_and_traits.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BareImportedCallWithMultipleBestTypeMatches_ReportsAmbiguousCallableOverload()
    {
        const string source = """
A :: module {
    pick :: Int -> Int
    {
        value => value + 1
    }
}

B :: module {
    pick :: Int -> Int
    {
        value => value + 2
    }
}

import A.*
import B.*

main :: Unit -> Int
{
    _ => pick(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ambiguous_callable_overload.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload 'pick'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("A.pick", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("B.pick", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BareImportedInfixCallWithMultipleBestTypeMatches_ReportsAmbiguousCallableOverload()
    {
        const string source = """
A :: module {
    join :: Int -> Int -> Int
    {
        left => right => left + right
    }
}

B :: module {
    join :: Int -> Int -> Int
    {
        left => right => left + right + 1
    }
}

import A.*
import B.*

main :: Unit -> Int
{
    _ => 1 `join` 2
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ambiguous_infix_callable_overload.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload 'join'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("A.join", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("B.join", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_PipeToBareImportedCallableWithMultipleBestTypeMatches_ReportsAmbiguousCallableOverload()
    {
        const string source = """
A :: module {
    pick :: Int -> Int
    {
        value => value + 1
    }
}

B :: module {
    pick :: Int -> Int
    {
        value => value + 2
    }
}

import A.*
import B.*

main :: Unit -> Int
{
    _ => 1 |> pick
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ambiguous_pipe_callable_overload.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload 'pick'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("A.pick", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("B.pick", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_MethodCallWithMultipleBestTypeMatches_ReportsAmbiguousCallableOverload()
    {
        const string source = """
A :: module {
    pick :: Int -> Int
    {
        value => value + 1
    }
}

B :: module {
    pick :: Int -> Int
    {
        value => value + 2
    }
}

import A.*
import B.*

main :: Unit -> Int
{
    _ => 1.pick()
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ambiguous_method_callable_overload.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload 'pick'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("A.pick", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("B.pick", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BareImportedValueWithoutCallStillReportsAmbiguity()
    {
        const string source = """
import std.Seq
import std.Option

f :: append;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous imported value 'append'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ExplicitPreludeWildcardImport_DoesNotDuplicateImplicitPreludeBindings()
    {
        const string source = """
import std.Prelude.*

main :: Unit -> Int
{
    _ => {
        xs := [1, 2, 3];
        len(xs)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Llvm,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ModuleImport_ExposesQualifiedTraitMethodsInsideNestedModule()
    {
        const string source = """
Probe :: module {
    import std.Applicative
    import std.Option
    import std.Traversable

    local_identity_applicative[A, G: kind2 : Applicative.Applicative[G]] :: G[A] -> G[A]
    {
        value => value
    }

    lift[A, G: kind2 : Applicative.Applicative[G]] :: A -> G[A]
    {
        value => Applicative.pure(value)
    }

    sequence_generic[A, T: kind2 : Traversable.Traversable[T], G: kind2 : Applicative.Applicative[G]] :: T[G[A]] -> G[T[A]]
    {
        values => Traversable.traverse(values)(local_identity_applicative)
    }

    main :: Unit -> Option[String]
    {
        _ => {
            value := Probe.lift("ok");
            values := Some(value);
            sequenced := sequence_generic(values);
            Option.unwrap_or(sequenced)(None())
        }
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Cannot resolve path 'Applicative.pure'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Cannot resolve path 'Traversable.traverse'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ModuleImport_ExposesNestedQualifiedTraitMethodsFromImportedModule()
    {
        const string source = """
import std.Traits

eq_self[T: Traits.Eq] :: T -> Bool
{
    value => Traits.Eq.eq(value)(value)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.TutorialExample("29_precompiled_stdlib.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Cannot resolve path 'Trait.Eq.eq'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CurrentModuleRelativeQualifiedTraitMethodPath_ResolvesWithoutStdImports()
    {
        const string source = """
Demo.Show :: module
{
    Show :: trait {
        show :: Self -> String
    }

    render[T: Show] :: T -> String
    {
        value => Show.show(value)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "current_module_relative_qualified_trait_method.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Cannot resolve path 'Show.show'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ModuleQualifiedTraitMethod_PrefersSameNamedTraitMemberOverImplMember()
    {
        const string source = """
Demo.Append :: module
{
    Append :: trait {
        append :: Self -> Self -> Self
    }

    append3[T: Append] :: T -> T -> T -> T
    {
        left => middle => right => Append.append(Append.append(left)(middle))(right)
    }


    append :: Int -> Int -> Int
     impl Append
{
        left => right => left + right
    }


    append :: String -> String -> String
     impl Append
{
        left => right => left
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "same_named_trait_member_over_impl_member.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));

        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var module = Assert.Single(
            root.Declarations.OfType<ModuleDecl>(),
            candidate => candidate.Path.SequenceEqual(["Demo", "Append"]));
        var append3 = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "append3");
        var inferredType = Assert.IsAssignableFrom<Eidosc.Types.Type>(append3.InferredType);

        Assert.False(inferredType.IsConcrete);
        Assert.NotEmpty(inferredType.FreeTypeVariables());
    }

    [Fact]
    public void CompilationPipeline_ImportedModuleQualifiedTraitMethod_PrefersSameNamedTraitMemberOverImplMember()
    {
        const string source = """
Lib.Semigroup :: module
{
    Semigroup :: trait {
        append :: Self -> Self -> Self
    }


    append :: Int -> Int -> Int
     impl Semigroup
{
        left => right => left + right
    }


    append :: String -> String -> String
     impl Semigroup
{
        left => right => left
    }
}

App :: module {
    import Lib.Semigroup

    append3[T: Semigroup] :: T -> T -> T -> T
    {
        left => middle => right => Semigroup.append(Semigroup.append(left)(middle))(right)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "imported_same_named_trait_member_over_impl_member.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));

        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var module = Assert.Single(
            root.Declarations.OfType<ModuleDecl>(),
            candidate => candidate.Path.SequenceEqual(["App"]));
        var append3 = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "append3");
        var inferredType = Assert.IsAssignableFrom<Eidosc.Types.Type>(append3.InferredType);

        Assert.False(inferredType.IsConcrete);
        Assert.NotEmpty(inferredType.FreeTypeVariables());
    }

    [Fact]
    public void CompilationPipeline_ImportedModuleFunction_ResolvesBesideSameNamedEffect()
    {
        const string source = """
Lib.Async :: module
{
    Task[A] :: type {value:: A}

    Async :: effect;

    spawn[A] :: (Unit -> A) -> Task[A]
    {
        thunk => Task{value: thunk(())}
    }
}

App :: module {
    import Lib.Async

    main :: Unit -> Int
    {
        _ => {
            task := Async.spawn(_ => 41);
            1
        }
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "imported_module_function_beside_same_named_effect.eidos",
            StopAtPhase = CompilationPhase.Effects,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_CurrentModuleRelativeQualifiedEffectPaths_ResolveWithoutStdImports()
    {
        const string source = """
Demo.Logger :: module
{
    Logger :: effect;

    log :: String -> Int need Logger
    {
        _ => 0
    }

    run :: String -> Int need Logger.Logger
    {
        _ => 0
    }

    main :: Unit -> Int need Logger.Logger
    {
        _ => {
            run("hello") + Logger.log("world")
        }
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "current_module_relative_qualified_effect_paths.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined effect 'Logger.Logger'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Cannot resolve path 'Logger.log'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TopLevelModuleFunctions_DoNotLeakAsBareNames()
    {
        const string source = """
A :: module {
    f :: Int -> Int
    {
        x => x + 1
    }
}

B :: module {
    f :: Int -> Int
    {
        x => x + 100
    }
}

main :: Unit -> Int
{
    _ => f(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_function_short_name_leak.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Undefined identifier 'f'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TopLevelModuleFunctions_DoNotLeakAsMethodShortNames()
    {
        const string source = """
A :: module {
    pick :: Int -> Int
    {
        x => x + 1
    }
}

B :: module {
    pick :: Int -> Int
    {
        x => x + 100
    }
}

main :: Unit -> Int
{
    _ => 1.pick()
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_method_short_name_leak.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined function 'pick'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload 'pick'", StringComparison.Ordinal));
    }

    private static string FormatCounters(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}"));
    }
}

