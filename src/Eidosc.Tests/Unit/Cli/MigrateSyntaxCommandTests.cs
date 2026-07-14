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
        // A qualified path is not a legacy cons expression. The 0.6 migration handles it
        // through the dedicated Namespace edit instead.
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

    [Fact]
    public void CreatePlan_FromPrevious_RewritesQualifiedNamesAndAdtSeparatorsOnly()
    {
        const string source = """
OptionI :: type {
    Some(Int) | None
}

answer::Int = Std::Option::unwrap_or(Some(42))(0);
""";
        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);

        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));
        Assert.Contains("OptionI :: type", rewritten, StringComparison.Ordinal);
        Assert.Contains("Some(Int) , None", rewritten, StringComparison.Ordinal);
        Assert.Contains("answer::Int = Std.Option.unwrap_or", rewritten, StringComparison.Ordinal);
        Assert.Equal(
            2,
            GetAllEdits(plan).Count(edit =>
                string.Equals(edit.Kind, "qualified-namespace-separator", StringComparison.Ordinal)));
        Assert.Single(
            GetAllEdits(plan),
            edit => string.Equals(edit.Kind, "adt-constructor-separator", StringComparison.Ordinal));
    }

    [Fact]
    public void CreatePlan_FromPreviousProject_PlansManifestAndSourceAtomically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eidos-migrate-project-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(sourceDir);
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var sourcePath = Path.Combine(sourceDir, "Main.eidos");
        File.WriteAllText(
            manifestPath,
            """
manifestSchema = 3

[language]
version = "0.5.0-alpha.1"
""");
        File.WriteAllText(sourcePath, "value :: Int = Std::Option::unwrap_or(None)(0);\n");

        try
        {
            var plan = SyntaxMigrationPlanner.CreatePlan(
                tempDir,
                EidosLanguageVersions.Previous,
                EidosLanguageVersions.Current);

            Assert.True(plan.ManifestNeedsUpdate);
            Assert.Equal(EidosLanguageVersions.Previous, plan.CurrentManifestSyntax);
            Assert.Equal("ready", plan.SourceRewriteStatus);
            Assert.Contains(
                GetAllEdits(plan),
                edit => string.Equals(edit.Kind, "qualified-namespace-separator", StringComparison.Ordinal));

            SyntaxMigrationPlanner.ApplyPlan(plan);
            Assert.Contains(
                $"version = \"{EidosLanguageVersions.Current}\"",
                File.ReadAllText(manifestPath),
                StringComparison.Ordinal);
            Assert.Contains("Std.Option.unwrap_or", File.ReadAllText(sourcePath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static (SyntaxMigrationPlan Plan, string SourceText) CreatePlanForSource(
        string source,
        string fromSyntax = EidosLanguageVersions.Legacy)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eidos-migrate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "input.eidos");
        File.WriteAllText(sourcePath, source);

        try
        {
            var plan = SyntaxMigrationPlanner.CreatePlan(sourcePath, fromSyntax, EidosLanguageVersions.Current);
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

    private static string ApplyEdits(string source, IEnumerable<SyntaxMigrationEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(static edit => edit.Start))
        {
            source = source.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        }

        return source;
    }
}
