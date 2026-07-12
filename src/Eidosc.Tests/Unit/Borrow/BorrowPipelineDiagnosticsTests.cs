using System;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class BorrowPipelineDiagnosticsTests
{
    [Fact]
    public void CompilationPipeline_MutateConflict_ReportsSinglePrimaryE1002()
    {
        const string source = """
main[A] :: Int -> A
{
    _ => {
        mut x := "hello";
        mut cond := x == "hello";
        x := "world";
        cond := false;
        0;
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_mutate_conflict.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "E1002");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message == "值被借用中，无法修改");
    }

    [Fact]
    public void CompilationPipeline_MutateConflict_DiagnosticCarriesAliasTraceIdNotes()
    {
        const string source = """
main[A] :: Int -> A
{
    _ => {
        mut x := "hello";
        mut cond := x == "hello";
        x := "world";
        cond := false;
        0;
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_mutate_conflict_trace.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var conflict = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "E1002");

        Assert.Contains(conflict.Notes, note => note.StartsWith("alias trace id: "));
        Assert.Contains(conflict.Notes, note => note.StartsWith("alias state lookup: "));
        Assert.Contains(conflict.Notes, note => note.StartsWith("alias trace: "));
    }

    [Fact]
    public void CompilationPipeline_PatternRefAfterMutBinding_ReportsBorrowConflict()
    {
        const string source = """
demo :: Int -> Int
{
    x => {
        mref y := x;
        ref z := x;
        x
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_pattern_ref_after_mut_conflict.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var conflict = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "E1002");

        Assert.Contains("不可变借用", conflict.Message, StringComparison.Ordinal);
        Assert.Contains(conflict.Notes, note => note.StartsWith("alias trace id: "));
        Assert.Contains(conflict.Notes, note => note.StartsWith("alias trace: "));
    }

    [Fact]
    public void CompilationPipeline_IndexRead_ThroughRefList_SucceedsWithoutExplicitDeref()
    {
        const string source = """
first :: Ref[Seq[Int]] -> Int
{
    xs => xs[0]
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_ref_list_index_autoderef.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_CallAcceptingRef_AllowsMRefArgumentWithoutExplicitConversion()
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_mref_to_ref_call.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_FieldRead_ThroughRefRecord_SucceedsWithoutExplicitDeref()
    {
        const string source = """
Range :: type {
    start: Int, end: Int
}

read :: Ref[Range] -> Int
{
    r => r.start
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_ref_record_field_autoderef.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_DotApplication_OnMRefReceiver_ToRefMethod_Succeeds()
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_mref_receiver_ref_method.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_DotApplication_OnRefReceiver_ToRefMethod_Succeeds()
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_ref_receiver_ref_method.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_FieldRead_ThenDotApplication_Succeeds()
    {
        const string source = """
Range :: type {
    start: Int, end: Int
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_field_read_then_dot_application.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_FieldThenDotApplication_OnRefRecordContainingRef_Succeeds()
    {
        const string source = """
ReaderBox[T] :: type {
    reader: Ref[T], tag: Int
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_field_then_dot_application_ref_record.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_FieldThenDotApplication_OnRefRecordContainingMRef_Succeeds()
    {
        const string source = """
WriterBox[T] :: type {
    writer: MRef[T], tag: Int
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_field_then_dot_application_mref_record.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_IndexThenFieldRead_OnRefListOfRecords_Succeeds()
    {
        const string source = """
Range :: type {
    start: Int, end: Int
}

first_start :: Ref[Seq[Range]] -> Int
{
    xs => xs[0].start
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_index_then_field_read_ref_list_of_records.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_IndexThenDotApplication_OnRefListOfRefs_Succeeds()
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_index_then_dot_application_ref_list.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_BareDotPrefersFieldRead_OverSameNamedZeroArgCall()
    {
        const string source = """
Range :: type {
    start: Int, end: Int
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

        var options = new CompilationOptions
        {
            InputFile = "pipeline_field_over_same_named_call.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ReturnDirectRefParam_Succeeds()
    {
        const string source = """
passthrough :: Ref[Int] -> Ref[Int]
{
    r => r
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_return_direct_ref_param.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ReturnBorrowFromFieldProjectionParam_Succeeds()
    {
        const string source = """
Box :: type {
    value: Int, tag: Int
}

borrow_value :: Ref[Box] -> Ref[Int]
{
    box => ref box.value
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_return_borrow_from_field_projection.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ReturnBorrowFromIndexProjectionParam_Succeeds()
    {
        const string source = """
borrow_first :: MRef[Seq[Int]] -> MRef[Int]
{
    xs => mref xs[0]
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_return_borrow_from_index_projection.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_ReturnBorrowFromLocalTemp_ReportsE1004()
    {
        const string source = """
leak :: Unit -> Ref[Int]
{
    _ => {
        mut x := 1;
        ref x
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "pipeline_return_borrow_from_local_temp.eidos",
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "E1004");

        Assert.Contains("返回借用必须直接来自输入参数", diagnostic.Message, StringComparison.Ordinal);
    }
}
