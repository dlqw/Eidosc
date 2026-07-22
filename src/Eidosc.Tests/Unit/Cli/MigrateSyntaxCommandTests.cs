using System.IO;
using System.CommandLine;
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
import std::Collection/Seq::{map, filter}
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
    public void CreatePlan_FromPreviousPrevious_RewritesQualifiedNamesAndAdtSeparatorsOnly()
    {
        const string source = """
OptionI :: type {
    Some(Int) | None
}

answer::Int = std::option::unwrap_or(Some(42))(0);
""";
        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.PreviousPrevious);

        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));
        Assert.Contains("OptionI :: type", rewritten, StringComparison.Ordinal);
        Assert.Contains("Some:: type(Int) , None :: type {}", rewritten, StringComparison.Ordinal);
        Assert.Contains("answer::Int = std.Option.unwrap_or", rewritten, StringComparison.Ordinal);
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
                EidosLanguageVersions.PreviousPrevious,
                EidosLanguageVersions.Current);

            Assert.True(plan.ManifestNeedsUpdate);
            Assert.Equal(EidosLanguageVersions.PreviousPrevious, plan.CurrentManifestSyntax);
            Assert.Equal("ready", plan.SourceRewriteStatus);
            Assert.Contains(
                GetAllEdits(plan),
                edit => string.Equals(edit.Kind, "qualified-namespace-separator", StringComparison.Ordinal));

            SyntaxMigrationPlanner.ApplyPlan(plan);
            Assert.Contains(
                $"version = \"{EidosLanguageVersions.Current}\"",
                File.ReadAllText(manifestPath),
                StringComparison.Ordinal);
            Assert.Contains("std.Option.unwrap_or", File.ReadAllText(sourcePath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CreatePlan_FromPrevious_RewritesVersion07NamesTypeBodiesAndClauses()
    {
        const string source = """
import std.Option

logging :: Unit -> Unit
    need io, ffi
{
    _ => ()
}

deriveMarker :: comptime Meta.DeriveInput -> Meta.Expansion {
    input => Meta.keep(input)
}


@[expand(deriveMarker)]
OptionI :: type
{
    Some:: type(Int),
    None :: type {},
}
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("import std.Option", rewritten, StringComparison.Ordinal);
        Assert.Contains("need io, ffi", rewritten, StringComparison.Ordinal);
        Assert.Contains("meta.Type -> meta.Items", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("Target[", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("Transformation", rewritten, StringComparison.Ordinal);
        Assert.Contains("@[expand(deriveMarker)]", rewritten, StringComparison.Ordinal);
        Assert.Contains("OptionI :: type", rewritten, StringComparison.Ordinal);
        Assert.Contains("Some:: type(Int)", rewritten, StringComparison.Ordinal);
        Assert.Contains("None :: type {}", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_RenamesKeywordConflictingStdModules()
    {
        const string source = "import Std.Fn\nimport Std.Trait\ncomposed :: Fn.compose(id)(id)\ntrait_name :: Trait.show(1)\n";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("import std.Functions", rewritten, StringComparison.Ordinal);
        Assert.Contains("import std.Traits", rewritten, StringComparison.Ordinal);
        Assert.Contains("Functions.compose", rewritten, StringComparison.Ordinal);
        Assert.Contains("Traits.show", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_AlreadyVersion07Source_IsIdempotent()
    {
        const string source = """
import std.Option

main :: Unit -> Int { _ => 0 }
""";

        var (plan, _) = CreatePlanForSource(source, EidosLanguageVersions.Previous);

        Assert.Empty(GetAllEdits(plan));
        Assert.All(plan.FilePlans, file => Assert.Equal("unchanged", file.Status));
    }

    [Fact]
    public void CreatePlan_FromPrevious_ClassifiesLegacyDeriveByBuiltinIdentityNotCasing()
    {
        const string source = """
@derive(Eq, URLCodec, deriveMarker)
Payload :: type { Payload(Int) }
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("@[derive(Eq), expand(URLCodec), expand(deriveMarker)]", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_UsesSchemaMigrationForNestedAttributesAndBodylessFfi()
    {
        const string source = """
Api :: module {
    @ffi("native/allocate")
    allocate :: Cfn[Int, Int];

    @cstruct
    @derive(Eq, codec)
    Payload :: type { Payload(Int) }
}
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("need ffi", rewritten, StringComparison.Ordinal);
        Assert.Contains("extern(c, library: \"native\", name: \"allocate\")", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("link_library", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("link_name", rewritten, StringComparison.Ordinal);
        Assert.Contains("@[extern(c, library: \"native\", name: \"allocate\")]", rewritten, StringComparison.Ordinal);
        Assert.Contains("@[repr(c), derive(Eq), expand(codec)]", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("@ffi", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("@derive", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_MigratesGeneratorAttributeThroughExpandSchema()
    {
        const string source = """
@generator(target, 1)
Payload :: type { Payload(Int) }
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("@[expand(generator(target, 1))]", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_LegacyBorrowAttributeBlocksAtomicOwnershipMigration()
    {
        const string source = """
@borrow(read, write)
update :: Payload -> Payload {
    value => value
}

main :: Payload -> Payload {
    value => update(value)
}
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);

        Assert.Equal("blocked", plan.SourceRewriteStatus);
        var file = Assert.Single(plan.FilePlans);
        Assert.Equal("blocked", file.Status);
        Assert.Empty(file.Edits);
        Assert.Contains(
            file.Diagnostics,
            diagnostic => diagnostic.Contains("Ref/MRef", StringComparison.Ordinal) &&
                          diagnostic.Contains("call sites", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => SyntaxMigrationPlanner.ApplyPlan(plan));
        Assert.Equal(source, sourceText);
    }

    [Fact]
    public void ApplyPlan_ProjectOwnershipBlockerPreventsAllDefinitionAndCallSiteWrites()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eidos-ownership-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var definitionPath = Path.Combine(tempDir, "Definition.eidos");
        var callSitePath = Path.Combine(tempDir, "CallSite.eidos");
        const string definition = "@borrow(read)\nconsume :: Payload -> Unit { _ => () }\n";
        const string callSite = "@derive(Eq)\nPayload :: type { Payload(Int) }\nmain :: Payload -> Unit { value => consume(value) }\n";
        File.WriteAllText(definitionPath, definition);
        File.WriteAllText(callSitePath, callSite);

        try
        {
            var plan = SyntaxMigrationPlanner.CreatePlan(
                tempDir,
                EidosLanguageVersions.Previous,
                EidosLanguageVersions.Current);

            Assert.Equal("blocked", plan.SourceRewriteStatus);
            Assert.Contains(plan.FilePlans, file => file.Status == "blocked");
            Assert.Contains(plan.FilePlans, file => file.Edits.Length > 0);
            Assert.Throws<InvalidOperationException>(() => SyntaxMigrationPlanner.ApplyPlan(plan));
            Assert.Equal(definition, File.ReadAllText(definitionPath));
            Assert.Equal(callSite, File.ReadAllText(callSitePath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AttachmentsCommand_ProvidesDedicatedDryRunSurfaceForVersion07()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eidos-migrate-clauses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "input.eidos");
        const string source = "@derive(Eq)\nPayload :: type { Payload(Int) }\n";
        File.WriteAllText(sourcePath, source);

        try
        {
            var exitCode = await MigrateCommand.Create().InvokeAsync([
                "attachments",
                sourcePath,
                "--to",
                EidosLanguageVersions.Current,
                "--dry-run"
            ]);

            Assert.Equal(0, exitCode);
            Assert.Equal(source, File.ReadAllText(sourcePath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreatePlan_FromPrevious_RenamesOnlyKnownMetaAndBuildMembers()
    {
        const string source = """
shape :: comptime Meta.typeInfo(User);
generated :: comptime Build.generatedSource(Emit, "generated");
custom_meta :: comptime Meta.userDefinedMember(User);
custom_build :: comptime Build.UserDefinedMember();
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("meta.shape_of(User)", rewritten, StringComparison.Ordinal);
        Assert.Contains("build.generated_source(Emit", rewritten, StringComparison.Ordinal);
        Assert.Contains("meta.userDefinedMember(User)", rewritten, StringComparison.Ordinal);
        Assert.Contains("build.UserDefinedMember()", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_SameNamedRecordConstructor_BecomesProductBody()
    {
        const string source = """
GameState :: type {
    GameState {
        score: Int,
        alive: Bool,
    }
}
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("score:: Int", rewritten, StringComparison.Ordinal);
        Assert.Contains("alive:: Bool", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("GameState :: type{", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("GameState {", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_NullaryGadtConstructor_UsesCaseClauseOrder()
    {
        const string source = """
Direction[A] :: type {
    North -> Direction[Int],
    East -> Direction[Bool],
}
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("North :: type case Direction[Int] {}", rewritten, StringComparison.Ordinal);
        Assert.Contains("East :: type case Direction[Bool] {}", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("case type", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_FromPrevious_RewritesFunctionImplClauseToNamedInstance()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {}

show :: Person -> String
impl Show
{
    _ => "person"
}
""";

        var (plan, sourceText) = CreatePlanForSource(source, EidosLanguageVersions.Previous);
        var rewritten = ApplyEdits(sourceText, GetAllEdits(plan));

        Assert.Contains("ShowPerson :: instance Show", rewritten, StringComparison.Ordinal);
        Assert.Contains("show :: Person -> String", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("impl Show", rewritten, StringComparison.Ordinal);
        var result = new CompilationPipeline(rewritten, new CompilationOptions
        {
            InputFile = "migrated_impl_instance.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
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
