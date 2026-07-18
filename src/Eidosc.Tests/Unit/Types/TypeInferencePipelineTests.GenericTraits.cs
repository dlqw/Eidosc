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
    public void Types_GenericCall_TraitBoundUnsatisfied_Fails()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

id[T: Marker] :: T -> T
{
    x => x
}

bad :: Int -> Int
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCall_TraitBoundUnsatisfied_DiagnosticPointsToCallSite()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

id[T: Marker] :: T -> T
{
    x => x
}

bad :: Int -> Int
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E2001" && item.Message.Contains("Marker", StringComparison.Ordinal));
        var callLine = FindLine(source, "x => id(x)");
        var definitionLine = FindLine(source, "id[T: Marker] :: T -> T");
        var label = Assert.Single(diagnostic.Labels);

        Assert.Equal(callLine, label.Span.Location.Line);
        Assert.NotEqual(definitionLine, label.Span.Location.Line);
    }

    [Fact]
    public void Types_PredeclaredGenericCall_TraitBoundUnsatisfied_Fails()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

bad :: Int -> Int
{
    x => id(x)
}

id[T: Marker] :: T -> T
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_PrecompiledStdTraitConstraint_WithBuiltinOperators_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_types_std_trait_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var entryFile = Path.Combine(tempDir, "main.eidos");
        const string source = """
import std.Traits
import std.Ordering
import std.Text

eq_self[T: Traits.Eq] :: T -> Bool
{
    x => x == x
}

ord_self[T: Traits.Ord] :: T -> Bool
{
    x => x <= x
}

main :: Unit -> Int
{
    _ => {
        lessLabel := Ordering.show(Ordering.compare_int(1)(2));
        echo := Text.show(Text.clone("ok"));

        if eq_self(41) &&
           ord_self('a') &&
           lessLabel == "Less" &&
           echo == "ok" then { 1 }
        else { 0 }
    }
}
""";

        File.WriteAllText(entryFile, source);

        try
        {
            var result = RunPipeline(source, CompilationPhase.Types, options => options.InputFile = entryFile);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Types_PrecompiledResultContains_FunctionValueWithoutEq_Fails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_types_result_contains_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var entryFile = Path.Combine(tempDir, "main.eidos");
        const string source = """
import std.Result

inc :: Int -> Int
{
    value => value + 1
}

main :: Unit -> Bool
{
    _ => Result.contains(Ok(inc))(inc)
}
""";

        File.WriteAllText(entryFile, source);

        try
        {
            var result = RunPipeline(source, CompilationPhase.Types, options => options.InputFile = entryFile);

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E2001" &&
                              diagnostic.Message.Contains("Function type does not implement trait 'Eq'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Types_PrecompiledOptionContains_FunctionValueWithoutEq_Fails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_types_option_contains_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var entryFile = Path.Combine(tempDir, "main.eidos");
        const string source = """
import std.Option

inc :: Int -> Int
{
    value => value + 1
}

main :: Unit -> Bool
{
    _ => Option.contains(Some(inc))(inc)
}
""";

        File.WriteAllText(entryFile, source);

        try
        {
            var result = RunPipeline(source, CompilationPhase.Types, options => options.InputFile = entryFile);

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E2001" &&
                              diagnostic.Message.Contains("Function type does not implement trait 'Eq'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Types_GenericCall_TraitBoundSatisfied_Succeeds()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Box :: type {
    Box:: type(Int)
}


mark :: Box -> Bool
 impl Marker
{
    x => true
}

id[T: Marker] :: T -> T
{
    x => x
}

good :: Box -> Box
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
    }

    [Fact]
    public void Types_GenericWhereClause_TraitBoundSatisfied_Succeeds()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Box :: type {
    Box:: type(Int)
}


mark :: Box -> Bool
 impl Marker
{
    x => true
}

id[T] :: T -> T where T: Marker
{
    x => x
}

good :: Box -> Box
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
    }

    [Fact]
    public void Types_GenericCall_TraitBoundTypeArgumentKindMismatch_Fails()
    {
        const string source = """
Box[A] :: type {
    Wrap:: type(A)
}

Functor[F: kind2] :: trait {
    fmap :: F[Int] -> Self
}

use[T: Functor[Int]] :: T -> T
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains(
                              "Kind mismatch for trait argument #1 ('F') of 'Functor'",
                              StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCall_TraitBoundWithTraitArgs_MatchingImpl_Succeeds()
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

id[T: Functor[Box]] :: T -> T
{
    x => x
}

good :: Person -> Person
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
    }

    [Fact]
    public void Types_GenericCall_TraitBoundWithTraitArgs_DifferentImplArgs_Fails()
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

Bag[A] :: type {
    Bag:: type(A)
}


fmap :: Person -> Bag[Int]
 impl Functor[Bag]
{
    p => Bag(1)
}

id[T: Functor[Box]] :: T -> T
{
    x => x
}

bad :: Person -> Person
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Functor", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCall_ModuleQualifiedTraitBoundWithTraitArgs_MatchingImpl_Succeeds()
    {
        const string source = """
Core :: module {
    Functor[F: kind2] :: trait {
        fmap :: Self -> F[Int]
    }
}

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}


fmap :: Person -> Box[Int]
 impl Core . Functor[Box]
{
    p => Box(1)
}

id[T: Core.Functor[Box]] :: T -> T
{
    x => x
}

good :: Person -> Person
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
    }

    [Fact]
    public void Types_GenericCall_ModuleQualifiedTraitBoundWithTraitArgs_DifferentImplArgs_Fails()
    {
        const string source = """
Core :: module {
    Functor[F: kind2] :: trait {
        fmap :: Self -> F[Int]
    }
}

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}

Bag[A] :: type {
    Bag:: type(A)
}


fmap :: Person -> Bag[Int]
 impl Core . Functor[Bag]
{
    p => Bag(1)
}

id[T: Core.Functor[Box]] :: T -> T
{
    x => x
}

bad :: Person -> Person
{
    x => id(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Functor", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCall_ModuleQualifiedTraitBoundWithTraitArgs_FromImportedModuleFile_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_types_trait_args_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleFile = Path.Combine(tempDir, "Core.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
Core :: module {
    Functor[F: kind2] :: trait {
        fmap :: Self -> F[Int]
    }
}
""";

        const string entrySource = """
import Core

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}


fmap :: Person -> Box[Int]
 impl Core . Functor[Box]
{
    p => Box(1)
}

id[T: Core.Functor[Box]] :: T -> T
{
    x => x
}

good :: Person -> Person
{
    x => id(x)
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Types,
                UseColors = false
            }).Run();

            Assert.True(result.Success);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Types_GenericCall_ModuleQualifiedTraitBoundWithTraitArgs_FromImportedModuleFile_DifferentImplArgs_Fails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_types_trait_args_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleFile = Path.Combine(tempDir, "Core.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
Core :: module {
    Functor[F: kind2] :: trait {
        fmap :: Self -> F[Int]
    }
}
""";

        const string entrySource = """
import Core

Person :: type {
    Person:: type(Int)
}

Box[A] :: type {
    Box:: type(A)
}

Bag[A] :: type {
    Bag:: type(A)
}


fmap :: Person -> Bag[Int]
 impl Core . Functor[Bag]
{
    p => Bag(1)
}

id[T: Core.Functor[Box]] :: T -> T
{
    x => x
}

bad :: Person -> Person
{
    x => id(x)
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Types,
                UseColors = false
            }).Run();

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E2001" &&
                              diagnostic.Message.Contains("Functor", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Types_GenericTypeAnnotation_MissingTypeArgument_Fails()
    {
        const string source = """
Option[T] :: type {
    Some:: type(T) , None :: type {}
}

bad :: Option -> Int
{
    value => 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type 'Option' expects 1 type argument(s), but got 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_NonGenericTypeAnnotation_WithTypeArgument_Fails()
    {
        const string source = """
bad :: Int[String] -> Int
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type 'Int' expects 0 type argument(s), but got 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericTypeParameter_ConflictingHigherKindUsage_Fails()
    {
        const string source = """
bad[T] :: T[Int] -> T
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Type parameter 'T' expects 1 type argument(s), but got 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericAdtCtor_TraitBoundUnsatisfied_Fails()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Box[T: Marker] :: type {
    Wrap:: type(T)
}

bad :: Int -> Box[Int]
{
    x => Wrap(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericAdtTypePath_TraitBoundUnsatisfied_Fails()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Box[T: Marker] :: type {
    Wrap:: type(T)
}

bad :: Box[Int] -> Int
{
    x => 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericAdtCtor_TraitBoundSatisfied_Succeeds()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Tagged :: type {
    Tagged:: type(Int)
}


mark :: Tagged -> Bool
 impl Marker
{
    x => true
}

Box[T: Marker] :: type {
    Wrap:: type(T)
}

good :: Tagged -> Box[Tagged]
{
    x => Wrap(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
    }

    [Fact]
    public void Types_GenericFunctionBody_PreservesPolymorphismAcrossCalls_Succeeds()
    {
        const string source = """
Box[T] :: type {
    Wrap:: type(T)
}

unbox[T] :: Box[T] -> T
{
    value => match value
    {
        Wrap(x) => x
    }
}

mixed :: String -> Int
{
    s => {
        a := unbox(Wrap(s));
        b := unbox(Wrap(1));
        0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_GenericFunctionBody_InferredTraitBoundIsEnforcedAtCallSite_Fails()
    {
        const string source = """
Box :: type {
    Box:: type(Int)
}

double[T] :: T -> T
{
    x => x + x
}

bad :: Box -> Box
{
    x => double(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Num", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericFunctionBody_InferredTraitBoundWithValidType_Succeeds()
    {
        const string source = """
double[T] :: T -> T
{
    x => x + x
}

ok :: Int -> Int
{
    x => double(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E2001");
    }

    [Fact]
    public void Types_GenericCall_ExplicitTypeArg_Succeeds()
    {
        const string source = """
id[T] :: T -> T
{
    x => x
}

use :: Int -> Int
{
    x => id[Int](x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_GenericCall_ExplicitTypeArg_ArityMismatch_Fails()
    {
        const string source = """
pair[A, B] :: A -> B -> (A, B)
{
    (a, b) => (a, b)
}

use :: Int -> (Int, Int)
{
    x => pair[Int](x)(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Expected 2 type argument(s), but got 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCall_ExplicitTypeArg_TypeMismatch_Fails()
    {
        const string source = """
id[T] :: T -> T
{
    x => x
}

bad :: Int -> Int
{
    x => id[String](x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("String", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCall_ExplicitTypeArg_TraitBoundUnsatisfied_Fails()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

id[T: Marker] :: T -> T
{
    x => x
}

bad :: Int -> Int
{
    x => id[Int](x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E2001" &&
                          diagnostic.Message.Contains("Marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCall_ExplicitTypeArg_LowersThroughHir_Succeeds()
    {
        const string source = """
id[T] :: T -> T
{
    x => x
}

use :: Int -> Int
{
    x => id[Int](x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4001");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }
}
