using System;
using System.IO;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_AutoDeref_RefInt_AutoDerfsForGenericParameter()
    {
        const string source = """
identity[T] :: T -> T
{
    x => x
}

test :: Ref[Int] -> Int
{
    r => identity(r)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_UnaryRef_OnFieldPlace_ReturnsRefType()
    {
        const string source = """
Box :: type {
    value:: Int, tag:: Int
}

borrow_value :: Ref[Box] -> Ref[Int]
{
    box => ref box.value
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "borrow_value");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var inferredResult = Assert.IsType<TyRef>(inferredType.Result);

        var innerType = Assert.IsType<TyCon>(inferredResult.Inner);
        Assert.Equal("Int", innerType.Name);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_UnaryMRef_OnIndexPlace_ReturnsMRefType()
    {
        const string source = """
borrow_first :: MRef[Seq[Int]] -> MRef[Int]
{
    xs => mref xs[0]
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "borrow_first");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var inferredResult = Assert.IsType<TyMutRef>(inferredType.Result);

        var innerType = Assert.IsType<TyCon>(inferredResult.Inner);
        Assert.Equal("Int", innerType.Name);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_UnaryRef_OnTemporaryExpression_ReportsE4000()
    {
        const string source = """
borrow_temp :: Int -> Ref[Int]
{
    x => ref (x + 1)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("stable place", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("ref", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_UnaryMRef_OnCallResult_ReportsE4000()
    {
        const string source = """
make :: Int -> Int
{
    x => x
}

borrow_temp :: Int -> MRef[Int]
{
    x => mref make(x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("stable place", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("mref", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_IndexRead_ThroughRefList_DoesNotRequireExplicitDeref()
    {
        const string source = """
first :: Ref[Seq[Int]] -> Int
{
    xs => xs[0]
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "first");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var inferredResult = Assert.IsType<TyCon>(inferredType.Result);

        Assert.Equal("Int", inferredResult.Name);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_IndexRead_ThroughMRefList_DoesNotRequireExplicitDeref()
    {
        const string source = """
first :: MRef[Seq[Int]] -> Int
{
    xs => xs[0]
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "first");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var inferredResult = Assert.IsType<TyCon>(inferredType.Result);

        Assert.Equal("Int", inferredResult.Name);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_CallAcceptingRef_AllowsMRefArgumentWithoutExplicitConversion()
    {
        const string source = """
read :: Ref[Int] -> Int
{
    r => r
}

use :: Int -> Int
{
    x => read(mref x)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_FieldRead_ThroughRefRecord_DoesNotRequireExplicitDeref()
    {
        const string source = """
Range :: type {
    start:: Int, end:: Int
}

read :: Ref[Range] -> Int
{
    r => r.start
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_DotApplication_OnMRefReceiver_ToRefMethod_Succeeds()
    {
        const string source = """
read :: Ref[Int] -> Int
{
    r => r
}

use :: Int -> Int
{
    x => (mref x).read
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_DotApplication_OnRefReceiver_ToRefMethod_Succeeds()
    {
        const string source = """
read :: Ref[Int] -> Int
{
    r => r
}

use :: Ref[Int] -> Int
{
    r => r.read
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_FieldRead_ThenDotApplication_Succeeds()
    {
        const string source = """
Range :: type {
    start:: Int, end:: Int
}

inc :: Int -> Int
{
    x => x + 1
}

use :: Ref[Range] -> Int
{
    r => r.start.inc
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_FieldThenDotApplication_OnRefRecordContainingRef_Succeeds()
    {
        const string source = """
ReaderBox[T] :: type {
    reader:: Ref[T], tag:: Int
}

read :: Ref[Int] -> Int
{
    r => r
}

use :: Ref[ReaderBox[Int]] -> Int
{
    box => box.reader.read
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_FieldThenDotApplication_OnRefRecordContainingMRef_Succeeds()
    {
        const string source = """
WriterBox[T] :: type {
    writer:: MRef[T], tag:: Int
}

read :: Ref[Int] -> Int
{
    r => r
}

use :: Ref[WriterBox[Int]] -> Int
{
    box => box.writer.read
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_IndexThenFieldRead_OnRefListOfRecords_Succeeds()
    {
        const string source = """
Range :: type {
    start:: Int, end:: Int
}

first_start :: Ref[Seq[Range]] -> Int
{
    xs => xs[0].start
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_IndexThenDotApplication_OnRefListOfRefs_Succeeds()
    {
        const string source = """
read :: Ref[Int] -> Int
{
    r => r
}

use :: Ref[Seq[Ref[Int]]] -> Int
{
    xs => xs[0].read
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_BareDotPrefersFieldRead_OverSameNamedZeroArgCall()
    {
        const string source = """
Range :: type {
    start:: Int, end:: Int
}

start :: Ref[Range] -> Bool
{
    _ => false
}

read_field :: Ref[Range] -> Int
{
    r => r.start
}

read_method :: Ref[Range] -> Bool
{
    r => r.start()
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_AutoDeref_RefInt_InArithmeticBinary()
    {
        const string source = """
add_refs :: Ref[Int] -> Ref[Int] -> Int
{
    r1 => r2 => r1 + r2
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_AutoDeref_MRefInt_InArithmeticBinary()
    {
        const string source = """
sub_refs :: MRef[Int] -> MRef[Int] -> Int
{
    r1 => r2 => r1 - r2
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_AutoDeref_RefInt_InComparisonBinary()
    {
        const string source = """
compare_refs :: Ref[Int] -> Ref[Int] -> Bool
{
    r1 => r2 => r1 == r2
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_AutoDeref_RefInt_InAssignment()
    {
        const string source = """
assign_ref :: Ref[Int] -> Int
{
    r => {
        mut x := 0;
        x := r;
        x
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_ExplicitDeref_MRefInt_Assignment()
    {
        const string source = """
replace :: MRef[Int] -> Int -> Int
{
    target => value => {
        *target := value;
        *target
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_ExplicitDeref_RefInt_Assignment_ReportsImmutableTarget()
    {
        const string source = """
replace :: Ref[Int] -> Int -> Int
{
    target => value => {
        *target := value;
        *target
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("dereferenced Ref", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_FieldPlaceAssignment_WithMutableParameterBinding_Succeeds()
    {
        const string source = """
Point :: type {
    x:: Int, y:: Int
}

bump :: Point -> Point
{
    mut point => {
        point.x := point.x + 1;
        point
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_FieldPlaceAssignment_WithImmutableParameterBinding_ReportsActionableDiagnostic()
    {
        const string source = """
Point :: type {
    x:: Int, y:: Int
}

bump :: Point -> Point
{
    point => {
        point.x := point.x + 1;
        point
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("immutable parameter 'point'", StringComparison.Ordinal) &&
                          diagnostic.Helps.Any(help => help.Contains("mut point =>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Types_AutoDeref_RefInt_DoesNotTriggerInBinary_WhenBothAreRef()
    {
        const string source = """
ref_eq :: Ref[Int] -> Ref[Int] -> Ref[Int]
{
    r1 => r2 => r1
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }
}
