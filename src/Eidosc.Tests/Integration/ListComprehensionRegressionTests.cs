using System.Collections.Concurrent;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Category, TestCategories.Slow)]
public class ListComprehensionRegressionTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;

    [Fact]
    public void AllFixtures_HirPhase_DoesNotEmitListComprehensionE5000()
    {
        var failures = RunFixtureScan(CompilationPhase.Hir);
        Assert.True(
            failures.Count == 0,
            $"Found list-comprehension E5000 diagnostics at HIR:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [Fact]
    public void AllFixtures_MirPhase_DoesNotEmitListComprehensionE5000()
    {
        var failures = RunFixtureScan(CompilationPhase.Mir);
        Assert.True(
            failures.Count == 0,
            $"Found list-comprehension E5000 diagnostics at MIR:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static List<string> RunFixtureScan(CompilationPhase stopPhase)
    {
        var fixtureRoot = TestSourceLoader.GetFullPath(Paths.FixtureSourceRoot);
        var fixtures = EidosFixtureInventory.ListComprehensionFixtureProjectPaths();

        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(fixtures, fixture =>
        {
            var file = TestSourceLoader.GetFullPath(fixture);
            var source = TestSourceLoader.Load(fixture);
            var result = new CompilationPipeline(source, new CompilationOptions
            {
                InputFile = file,
                StopAtPhase = stopPhase,
                UseColors = false
            }).Run();

            var relativePath = Path.GetRelativePath(fixtureRoot, file);
            var errors = result.Diagnostics
                .Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error)
                .ToArray();

            if (!result.Success || result.CompletedPhase != stopPhase || errors.Length > 0)
            {
                failures.Add(
                    $"{relativePath}: expected {stopPhase}, completed {result.CompletedPhase}, success={result.Success}, errors={FormatDiagnostics(errors)}");
            }

            foreach (var diagnostic in result.Diagnostics.Where(IsListComprehensionE5000))
            {
                failures.Add($"{relativePath}: {diagnostic.Code} {diagnostic.Message}");
            }
        });

        return failures
            .OrderBy(failure => failure, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsListComprehensionE5000(Diagnostic.Diagnostic diagnostic)
    {
        if (diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) != true)
        {
            return false;
        }

        return diagnostic.Message?.Contains("ListComprehension", StringComparison.Ordinal) == true;
    }

    private static string FormatDiagnostics(IReadOnlyCollection<Diagnostic.Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "<none>";
        }

        return string.Join("; ", diagnostics.Select(static diagnostic => $"{diagnostic.Code} {diagnostic.Message}"));
    }
}
