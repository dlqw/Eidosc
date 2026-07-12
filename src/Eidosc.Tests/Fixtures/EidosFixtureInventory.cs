using Eidosc.Pipeline;

namespace Eidosc.Tests.Fixtures;

public readonly record struct EidosFixtureCase(
    string RelativePath,
    CompilationPhase MinimumStage,
    string Capability,
    string Reason)
{
    public string ProjectRelativePath => TestPathConfig.Current.Fixture(RelativePath);
}

public readonly record struct EidosErrorFixtureCase(
    string RelativePath,
    string[] ExpectedCodes)
{
    public string ProjectRelativePath => TestPathConfig.Current.Fixture(RelativePath);
}

[Flags]
public enum EidosFixtureCoverageLayer
{
    None = 0,
    BorrowSweep = 1 << 0,
    ErrorDiagnostic = 1 << 1,
    LlvmIr = 1 << 2,
    NativeSmoke = 1 << 3,
    StdlibSurface = 1 << 4,
    RuntimeSurface = 1 << 5
}

public readonly record struct EidosFixtureCoverage(
    string RelativePath,
    EidosFixtureCoverageLayer Layers,
    string Owner,
    string KeepReason)
{
    public string ProjectRelativePath => TestPathConfig.Current.Fixture(RelativePath);
}

public static class EidosFixtureInventory
{
    private static readonly Lazy<IReadOnlyList<string>> ListComprehensionFixtureProjectPathCache = new(
        LoadListComprehensionFixtureProjectPaths,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<string>> StdlibPrecompiledFileCache = new(
        LoadStdlibPrecompiledFiles,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<string>> StdlibFixtureFileCache = new(
        LoadStdlibFixtureFiles,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public const string Basic = "basic";
    public const string Control = "control";
    public const string LegacyAbility = "ability";
    public const string Effect = "effect";
    public const string Closure = "closure";
    public const string Stdlib = "stdlib";
    public const string Ffi = "ffi";
    public const string TypeSystem = "type-system";
    public const string Borrow = "borrow";
    public const string Trait = "trait";
    public const string ListComprehension = "list-comprehension";

    public static IReadOnlyList<EidosFixtureCase> BorrowPhaseSuccessFixtures { get; } =
    [
        Success("basic/arithmetic.eidos", Basic),
        Success("basic/empty.eidos", Basic),
        Success("basic/functions.eidos", Basic),
        Success("basic/identifiers.eidos", Basic),
        Success("basic/literals.eidos", Basic),
        Success("basic/simple.eidos", Basic),
        Success("control/list_comp.eidos", ListComprehension),
        Success("control/list_comp_func.eidos", ListComprehension),
        Success("control/list_comp_main.eidos", ListComprehension),
        Success("control/match_scrutinee_string.eidos", Control),
        Success("control/view_guard_list_pattern.eidos", Control),
        Success("effects/basic_effect.eidos", Effect),
        Success("closures/capture.eidos", Closure),
        Success("closures/composition.eidos", Closure),
        Success("closures/currying.eidos", Closure),
        Success("stdlib/std_fn_import.eidos", Stdlib),
        Success("stdlib/std_fn_plus.eidos", Stdlib),
        Success("stdlib/std_ffi_import.eidos", Stdlib),
        Success("stdlib/std_float_math_import.eidos", Stdlib),
        Success("stdlib/std_list_import.eidos", Stdlib),
        Success("stdlib/std_list_plus.eidos", Stdlib),
        Success("stdlib/std_ordering_import.eidos", Stdlib),
        Success("stdlib/std_let_question_binding.eidos", Stdlib),
        Success("stdlib/std_option_import.eidos", Stdlib),
        Success("stdlib/std_result_import.eidos", Stdlib),
        Success("stdlib/std_regex_import.eidos", Stdlib),
        Success("stdlib/std_range_import.eidos", Stdlib),
        Success("stdlib/std_prelude_import.eidos", Stdlib),
        Success("stdlib/std_prelude_core_import.eidos", Stdlib),
        Success("stdlib/std_prelude_fn_plus.eidos", Stdlib),
        Success("stdlib/std_predicate_view_pattern.eidos", Stdlib),
        Success("stdlib/std_trait_import.eidos", Stdlib),
        Success("stdlib/std_text_import.eidos", Stdlib),
        Success("ffi/std_ffi_safe_helpers.eidos", Ffi),
        Success("types/generic_signature.eidos", TypeSystem),
        Success("types/generics.eidos", TypeSystem),
        Success("types/generic_nonzero_partial_indirect.eidos", TypeSystem),
        Success("types/generic_nonzero_partial_trait_copy_ctor_field_indirect.eidos", TypeSystem),
        Success("types/generic_nonzero_partial_trait_copy_deref_indirect.eidos", TypeSystem),
        Success("types/generic_nonzero_partial_trait_copy_dynamic_index_indirect.eidos", TypeSystem),
        Success("types/generic_nonzero_partial_trait_copy_indirect.eidos", TypeSystem),
        Success("types/generic_zero_arg_partial_indirect.eidos", TypeSystem),
        Success("types/inference.eidos", TypeSystem),
        Success("borrow/valid/affine_consume.eidos", Borrow),
        Success("borrow/valid/basic_move.eidos", Borrow),
        Success("borrow/valid/borrow_shared.eidos", Borrow),
        Success("borrow/valid/generic_nonzero_partial_ctor_field_move_ok.eidos", Borrow),
        Success("borrow/valid/last_use.eidos", Borrow),
        Success("borrow/valid/projection_alias_cross_stage.eidos", Borrow),
        Success("traits/basic_impl.eidos", Trait),
        Success("traits/basic_trait.eidos", Trait),
        Success("traits/generic_impl.eidos", Trait),
        Success("traits/multi_impl.eidos", Trait),
        Success("traits/trait_constraint.eidos", Trait)
    ];

    public static IReadOnlyList<EidosErrorFixtureCase> ErrorFixtures { get; } =
    [
        Error("abilities/errors/missing_ability.eidos", "E3000"),
        Error("basic/unresolved.eidos", "E3000"),
        Error("errors/type_mismatch.eidos", "E4000"),
        Error("traits/trait_error.eidos", "E2001"),
        Error("borrow/errors/affine_reuse.eidos", "E1001"),
        Error("borrow/errors/double_move.eidos", "E1001"),
        Error("borrow/errors/generic_nonzero_partial_deref_noncopy_error.eidos", "E1002", "E1004"),
        Error("borrow/errors/generic_nonzero_partial_dynamic_index_noncopy_error.eidos", "E1002", "E1004"),
        Error("borrow/errors/mutable_borrow_conflict.eidos", "E1002"),
        Error("borrow/errors/use_after_move.eidos", "E1001")
    ];

    public static IReadOnlyList<EidosFixtureCoverage> CoverageMatrix { get; } =
    [
        Coverage(
            "stdlib/std_list_import.eidos",
            EidosFixtureCoverageLayer.BorrowSweep |
            EidosFixtureCoverageLayer.LlvmIr |
            EidosFixtureCoverageLayer.NativeSmoke |
            EidosFixtureCoverageLayer.StdlibSurface,
            "Stdlib/List",
            "Canonical List import fixture; keep one borrow sweep, one import.LLVM assertion, and one native smoke entry."),
        Coverage(
            "control/list_comp_main.eidos",
            EidosFixtureCoverageLayer.BorrowSweep |
            EidosFixtureCoverageLayer.LlvmIr |
            EidosFixtureCoverageLayer.NativeSmoke,
            "Language/ListComprehension",
            "Main list-comprehension fixture covers phase sweep plus backend materialization."),
        Coverage(
            "stdlib/std_network_import.eidos",
            EidosFixtureCoverageLayer.LlvmIr |
            EidosFixtureCoverageLayer.NativeSmoke |
            EidosFixtureCoverageLayer.RuntimeSurface,
            "Stdlib/Network",
            "Network import fixture needs IR coverage and libcurl-gated local runtime shape checks."),
        Coverage(
            "stdlib/std_result_import.eidos",
            EidosFixtureCoverageLayer.BorrowSweep |
            EidosFixtureCoverageLayer.LlvmIr |
            EidosFixtureCoverageLayer.NativeSmoke |
            EidosFixtureCoverageLayer.StdlibSurface,
            "Stdlib/Result",
            "Result import fixture anchors typeclass specialization and traversable native smoke coverage.")
    ];

    public static IEnumerable<object[]> BorrowPhaseSuccessTheoryData() =>
        BorrowPhaseSuccessFixtures.Select(static fixture => new object[] { fixture.ProjectRelativePath });

    public static IEnumerable<object[]> ErrorTheoryData() =>
        ErrorFixtures.Select(static fixture => new object[] { fixture.ProjectRelativePath, fixture.ExpectedCodes });

    public static IEnumerable<object[]> FixtureCoverageTheoryData() =>
        CoverageMatrix.Select(static coverage => new object[] { coverage });

    public static EidosFixtureCoverage? FindCoverage(string relativePath)
    {
        foreach (var coverage in CoverageMatrix)
        {
            if (string.Equals(coverage.RelativePath, relativePath, StringComparison.Ordinal))
            {
                return coverage;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ListComprehensionFixtureProjectPaths() =>
        ListComprehensionFixtureProjectPathCache.Value;

    public static IReadOnlyList<string> StdlibPrecompiledFiles() =>
        StdlibPrecompiledFileCache.Value;

    public static IReadOnlyList<string> StdlibFixtureFiles() =>
        StdlibFixtureFileCache.Value;

    private static IReadOnlyList<string> LoadListComprehensionFixtureProjectPaths() =>
        EnumerateFixtureFiles()
            .Where(static file => File.ReadAllText(file.FullPath).Contains("<-", StringComparison.Ordinal))
            .OrderBy(static file => file.ProjectRelativePath, StringComparer.Ordinal)
            .Select(static file => file.ProjectRelativePath)
            .ToArray();

    private static IReadOnlyList<string> LoadStdlibPrecompiledFiles()
    {
        var stdlibDir = FindStdlibDir();
        return Directory.GetFiles(stdlibDir, "*.eidos")
            .OrderBy(static file => Path.GetFileName(file), StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> LoadStdlibFixtureFiles()
    {
        var fixtureStdlibDir = TestSourceLoader.GetFullPath(TestPathConfig.Current.Fixture("stdlib"));
        return Directory.GetFiles(fixtureStdlibDir, "*.eidos")
            .OrderBy(static file => Path.GetFileName(file), StringComparer.Ordinal)
            .ToArray();
    }

    private static EidosFixtureCase Success(string relativePath, string capability) =>
        new(
            relativePath,
            CompilationPhase.Borrow,
            capability,
            "Canonical borrow-phase fixture sweep coverage.");

    private static EidosErrorFixtureCase Error(string relativePath, params string[] expectedCodes) =>
        new(relativePath, expectedCodes);

    private static EidosFixtureCoverage Coverage(
        string relativePath,
        EidosFixtureCoverageLayer layers,
        string owner,
        string keepReason) =>
        new(relativePath, layers, owner, keepReason);

    private static IEnumerable<(string ProjectRelativePath, string FullPath)> EnumerateFixtureFiles()
    {
        var paths = TestPathConfig.Current;
        var fixtureRoot = TestSourceLoader.GetFullPath(paths.FixtureSourceRoot);

        return Directory.EnumerateFiles(fixtureRoot, "*.eidos", SearchOption.AllDirectories)
            .Select(file =>
            {
                var relativePath = Path.GetRelativePath(fixtureRoot, file).Replace('\\', '/');
                return (paths.Fixture(relativePath), file);
            });
    }

    private static string FindStdlibDir()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "Stdlib", "Precompiled", "Std");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent is null)
            {
                break;
            }

            dir = parent;
        }

        var projectDir = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Eidosc",
                "Stdlib",
                "Precompiled",
                "Std"));

        if (Directory.Exists(projectDir))
        {
            return projectDir;
        }

        throw new DirectoryNotFoundException("Stdlib directory not found.");
    }
}
