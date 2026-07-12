using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Category, TestCategories.Slow)]
public class MirOptimizationPipelineTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;
    private static readonly string FixturePath = Paths.Fixture("control/list_comp_func.eidos");

    [Fact]
    public void BorrowPhase_WithMirOptimizationEnabled_CompletesWithoutErrors()
    {
        var result = RunFixture(enableMirOpt: true, stopAtPhase: CompilationPhase.Borrow);

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);
        Assert.NotNull(result.MirModule);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void BorrowPhase_WithAndWithoutMirOptimization_KeepsErrorCodeSetEquivalent()
    {
        var baseline = RunFixture(enableMirOpt: false, stopAtPhase: CompilationPhase.Borrow);
        var optimized = RunFixture(enableMirOpt: true, stopAtPhase: CompilationPhase.Borrow);

        Assert.Equal(baseline.CompletedPhase, optimized.CompletedPhase);

        var baselineErrorCodes = baseline.Diagnostics
            .Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error)
            .Select(diagnostic => diagnostic.Code ?? diagnostic.Message)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
        var optimizedErrorCodes = optimized.Diagnostics
            .Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error)
            .Select(diagnostic => diagnostic.Code ?? diagnostic.Message)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(baselineErrorCodes, optimizedErrorCodes);
    }

    [Fact]
    public void MirPhase_DebugOutput_ContainsOptimizationSummaryWhenEnabled()
    {
        var source = TestSourceLoader.Load(FixturePath);
        var inputPath = TestSourceLoader.GetFullPath(FixturePath);
        var debugOutputPath = Path.Combine(Path.GetTempPath(), $"eidosc_mir_opt_{Guid.NewGuid():N}");

        try
        {
            var options = new CompilationOptions
            {
                InputFile = inputPath,
                StopAtPhase = CompilationPhase.Mir,
                EnableMirOptimizations = true,
                DebugOutputPath = debugOutputPath,
                UseColors = false
            };

            var result = new CompilationPipeline(source, options).Run();
            Assert.True(result.Success);

            var mirDebugPath = Path.Combine(debugOutputPath, "07_mir");
            var summaryPath = Path.Combine(mirDebugPath, "mir_optimization.txt");
            var beforePath = Path.Combine(mirDebugPath, "mir_before_opt.txt");

            Assert.True(File.Exists(summaryPath));
            Assert.True(File.Exists(beforePath));

            var summary = File.ReadAllText(summaryPath);
            Assert.Contains("enabled: True", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("applied: True", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DeadCodeElimination", summary, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(debugOutputPath))
            {
                try
                {
                    Directory.Delete(debugOutputPath, recursive: true);
                }
                catch
                {
                    // Ignore cleanup failures on CI/Windows locks.
                }
            }
        }
    }

    [Fact]
    public void MirPhase_GenericCallWithOptimization_RecordsDirtyWorklistConvergence()
    {
        const string source = """
id[T] :: T -> T
{
    value => value
}

main :: Unit -> Int
{
    _ => id[Int](1)
}
""";
        var debugOutputPath = Path.Combine(Path.GetTempPath(), $"eidosc_mir_opt_generic_{Guid.NewGuid():N}");

        try
        {
            var result = new CompilationPipeline(source, new CompilationOptions
            {
                InputFile = "mir_opt_generic_specialization.eidos",
                StopAtPhase = CompilationPhase.Mir,
                EnableMirOptimizations = true,
                DebugOutputPath = debugOutputPath,
                UseColors = false
            }).Run();

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

            var summaryPath = Path.Combine(debugOutputPath, "07_mir", "mir_optimization.txt");
            Assert.True(File.Exists(summaryPath));

            var summary = File.ReadAllText(summaryPath);
            Assert.Contains("enabled: True", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("applied: True", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("specializer_runs: 1", summary, StringComparison.Ordinal);
            Assert.Contains("specializer_changed_iterations: 1", summary, StringComparison.Ordinal);
            Assert.Contains("optimizer_changed_iterations: 1", summary, StringComparison.Ordinal);
            Assert.Contains("specialization_loop_convergence: dirty-worklist-local-optimizer", summary, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(debugOutputPath))
            {
                try
                {
                    Directory.Delete(debugOutputPath, recursive: true);
                }
                catch
                {
                    // Ignore cleanup failures on CI/Windows locks.
                }
            }
        }
    }

    private static CompilationResult RunFixture(bool enableMirOpt, CompilationPhase stopAtPhase)
    {
        var source = TestSourceLoader.Load(FixturePath);
        var options = new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(FixturePath),
            StopAtPhase = stopAtPhase,
            EnableMirOptimizations = enableMirOpt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
