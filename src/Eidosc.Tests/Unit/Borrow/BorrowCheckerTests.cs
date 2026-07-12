using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class BorrowCheckerTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;

    [Fact]
    public void CompilationPipeline_BorrowValidFixture_CompletesBorrowPhaseWithoutErrors()
    {
        var result = RunFixture(Paths.Fixture("borrow/valid/borrow_shared.eidos"));

        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_MutableBorrowConflictFixture_ReportsE1002()
    {
        var result = RunFixture(
            Paths.Fixture("borrow/errors/mutable_borrow_conflict.eidos"),
            stopAtBorrow: false,
            isolateSingleFile: true);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E1002");
    }

    [Fact]
    public void CompilationPipeline_InvalidSyntax_ReportsParserErrorInsteadOfTypeMismatch()
    {
        const string source = "main :: Int -> Int { _ => ??? }";
        var options = new CompilationOptions
        {
            InputFile = "borrow_invalid_syntax.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            UseColors = false,
            LanguageVersion = EidosLanguageVersions.Current
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code is "E4000" or "E4001");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E0001");
    }

    [Fact]
    public void CompilationPipeline_EarlyReturnAcrossOwnedStringBranches_DoesNotReportBorrowOrMoveErrors()
    {
        const string source = """
keep :: String -> String
{
    x => if true then { return x } else { return x }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "borrow_early_return_string.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            UseColors = false,
            LanguageVersion = EidosLanguageVersions.Current
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code is "E1001" or "E1002" or "E1004");
    }

    [Fact]
    public void CompilationPipeline_NonCopyConditionalMoveInMonotonicIndexLoop_DoesNotReportMoveErrors()
    {
        const string source = """
import Std::RuntimeArray
import Std::Seq

replace_bucket :: Seq[Seq[(Int, Int)]] -> Int -> Seq[(Int, Int)] -> Seq[Seq[(Int, Int)]]
{
    buckets => index => newBucket => {
        len := Seq::len(buckets)
        mut built := RuntimeArray::empty[Seq[(Int, Int)]]()
        mut i := 0
        loop {
            if i >= len then {
                break
            } else {
                value := if i == index then newBucket else buckets[i]
                built := RuntimeArray::push(built)(value)
                i := i + 1
            }
        }
        built
    }
}
""";

        var result = RunSource(source);

        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code is "E1001" or "E1002" or "E1004");
    }

    [Fact]
    public void CompilationPipeline_NonCopyConditionalMoveWithoutOneShotGuard_StillReportsMoveError()
    {
        const string source = """
import Std::RuntimeArray

invalid_repeat :: Seq[Seq[(Int, Int)]] -> Int -> Seq[(Int, Int)] -> Seq[Seq[(Int, Int)]]
{
    buckets => index => newBucket => {
        mut built := RuntimeArray::empty[Seq[(Int, Int)]]()
        mut i := 0
        loop {
            if i >= 2 then {
                break
            } else {
                value := if index == 0 then newBucket else buckets[0]
                built := RuntimeArray::push(built)(value)
                i := i + 1
            }
        }
        built
    }
}
""";

        var result = RunSource(source);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E1001");
    }

    [Fact]
    public void CompilationPipeline_LoopConsumeAndRebindNonCopyState_DoesNotReportMoveErrors()
    {
        const string source = """
import Std::Option

Box :: type
{
    Box(Int)
}

step :: Box -> Option[(Int, Box)]
{
    Box(value) =>
        if value >= 3
        then { None() }
        else { Some((value, Box(value + 1))) }
}

main :: Unit -> Int
{
    _ => {
        mut queue := Box(0)
        mut total := 0
        loop {
            match step(queue) {
                None() => break,
                Some((value, rest)) => {
                    total := total + value
                    queue := rest
                }
            }
        }
        total
    }
}
""";

        var result = RunSource(source);

        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code is "E1001" or "E1002" or "E1004");
    }

    private static CompilationResult RunFixture(
        string relativePath,
        bool stopAtBorrow = true,
        bool isolateSingleFile = false)
    {
        var source = TestSourceLoader.Load(relativePath);
        var options = new CompilationOptions
        {
            InputFile = isolateSingleFile ? Path.GetFileName(relativePath) : TestSourceLoader.GetFullPath(relativePath),
            UseColors = false,
            LanguageVersion = TestSourceLoader.GetLanguageVersion(relativePath)
        };
        if (stopAtBorrow)
        {
            options.StopAtPhase = CompilationPhase.Borrow;
        }

        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunSource(string source)
    {
        return CompilationHelper.Source(source, TestSourceLoader.GetFullPath(Paths.Fixture("basic/__inline_borrow_checker.eidos")))
            .ToPhase(CompilationPhase.Borrow)
            .Run();
    }
}

