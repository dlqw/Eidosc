using System.IO;
using Eidosc.Cli.Commands.Migrate;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class MigrateSyntaxCommandTests
{
    [Fact]
    public void CreatePlan_RewritesLegacySlashImportPathToDot()
    {
        const string source = """
import Std::Collection/Seq::{map, filter}
""";
        var (plan, _) = CreatePlanForSource(source);

        var slashEdits = GetAllEdits(plan)
            .Where(e => string.Equals(e.Replacement, ".", StringComparison.Ordinal) &&
                        string.Equals(e.Kind, "import-module-path", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(slashEdits);
    }

    [Fact]
    public void CreatePlan_RewritesWhitespaceSeparatedConsToPrepend()
    {
        // `head :: tail` (whitespace around ::) is legacy cons and migrates to `head +: tail`.
        // Path qualifiers like `Seq::cons` (no whitespace) must NOT be rewritten.
        const string source = """
build :: [Int] -> [Int] {
    xs => 1 +: xs
}
""";
        var (plan, sourceText) = CreatePlanForSource(source);

        // The `1 +: xs` is already modern; migration should not touch it. Instead verify with
        // a legacy cons source that whitespace-around `::` migrates to `+:`.
        const string legacySource = """
build :: [Int] -> [Int] {
    xs => 1 :: xs
}
""";
        var (legacyPlan, _) = CreatePlanForSource(legacySource);

        var consEdits = GetAllEdits(legacyPlan)
            .Where(e => string.Equals(e.Replacement, "+:", StringComparison.Ordinal) &&
                        string.Equals(e.Kind, "cons-operator", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(consEdits);
    }

    [Fact]
    public void CreatePlan_DoesNotRewritePathQualifierCons()
    {
        // `Seq::cons` is a path qualifier (no whitespace around ::), must stay as `::`.
        const string source = """
f :: [Int] -> Int {
    xs => Seq::length(xs)
}
""";
        var (plan, _) = CreatePlanForSource(source);

        var consEdits = GetAllEdits(plan)
            .Where(e => string.Equals(e.Kind, "cons-operator", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(consEdits);
    }

    private static (SyntaxMigrationPlan Plan, string SourceText) CreatePlanForSource(string source)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eidos-migrate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "input.eidos");
        File.WriteAllText(sourcePath, source);

        try
        {
            var plan = SyntaxMigrationPlanner.CreatePlan(sourcePath, EidosLanguageVersions.Legacy, EidosLanguageVersions.Current);
            return (plan, source);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static IEnumerable<SyntaxMigrationEdit> GetAllEdits(SyntaxMigrationPlan plan)
    {
        foreach (var filePlan in plan.FilePlans)
        {
            foreach (var edit in filePlan.Edits)
            {
                yield return edit;
            }
        }
    }
}
