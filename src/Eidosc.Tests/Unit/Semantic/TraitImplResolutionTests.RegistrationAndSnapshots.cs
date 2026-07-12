using Eidosc.Symbols;
using System.Reflection;
using Eidosc;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class TraitImplResolutionTests
{
    [Fact]
    public void NameResolver_ImplTypeRefKeyShape_PrefersStructuredIdentityOverText()
    {
        var resolver = new NameResolver(new SymbolTable());
        var method = typeof(NameResolver)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(methodInfo =>
                methodInfo.Name == "BuildImplTypeShapeNode" &&
                methodInfo.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(ImplTypeRefKey));
        var symbolId = new SymbolId(123);
        var typeId = new TypeId(456);
        var key = new ImplTypeRefKey(symbolId, typeId, "MisleadingName", []);

        var shape = Assert.IsType<ImplConstructorShapeNode>(method.Invoke(resolver, [key]));

        Assert.Equal($"type:{typeId.Value}", shape.Name);
        Assert.Equal(symbolId, shape.SymbolId);
        Assert.Equal(typeId, shape.TypeId);
    }

    [Fact]
    public void CompilationPipeline_ImplAttribute_RegistersTraitImplementation()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person(String)
}

@impl(Show)
show :: Person -> String
{
    p => "person"
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_impl_attr_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Show");
        var personId = symbolTable.LookupType("Person");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);

        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
        var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value);

        Assert.NotNull(impl);
        var implementingShape = Assert.IsType<ImplConstructorShapeNode>(impl!.ImplementingTypeShape);
        Assert.Equal("Person", implementingShape.Name);
        Assert.Empty(impl.TraitTypeArgShapes);
    }

    [Fact]
    public void CompilationPipeline_NameFirstInstance_RegistersTraitImplementation()
    {
        const string source = """
Show :: trait
{
    show :: Self -> String;
}

Person :: type
{
    Person(String)
}

ShowPerson :: instance Show
{
    show :: Person -> String
    {
        p => "person"
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_instance_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Show");
        var personId = symbolTable.LookupType("Person");
        Assert.True(traitId.HasValue);
        Assert.True(personId.HasValue);

        var personSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(personId.Value));
        var impl = symbolTable.LookupImplForTrait(personSymbol.TypeId, traitId.Value);

        Assert.NotNull(impl);
        var method = Assert.Single(impl!.Methods);
        Assert.True(method.IsValid);
        Assert.Single(impl.TraitMethodImplementations);
    }

    [Fact]
    public void CompilationPipeline_NameFirstConstructorBridgeInstance_GeneratesTraitImplementation()
    {
        const string source = """
Pos :: type {
    Pos(Int, Int)
}

DirectionInfo :: trait
{
    opposite :: Self -> Self
}

Direction :: type
{
    North |
    South
}

DirectionInfoDirection :: instance DirectionInfo for Direction
{
    North => { opposite = South() } |
    South => { opposite = North() }
}

read_opposite :: Direction -> Direction
{
    dir => opposite(dir)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_constructor_bridge_instance_ok.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupTrait("DirectionInfo");
        var directionId = symbolTable.LookupType("Direction");
        Assert.True(traitId.HasValue);
        Assert.True(directionId.HasValue);

        var directionSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(directionId.Value));
        var impl = symbolTable.LookupImplForTrait(directionSymbol.TypeId, traitId.Value);
        Assert.NotNull(impl);
        Assert.Single(impl!.Methods);
        Assert.Single(impl.TraitMethodImplementations);
    }

    [Fact]
    public void CompilationPipeline_TraitCheckSnapshot_RestoresGroundTraitQueries()
    {
        const string source = """
Eq :: trait
{
    eq :: Self -> Self -> Bool;
}

Box :: type
{
    Box(Int)
}

@impl(Eq)
eq :: Box -> Box -> Bool
{
    _ => _ => true
}

requires_eq[T: Eq] :: T -> T -> Bool
{
    a => b => true
}

same_box :: Box -> Box -> Bool
{
    a => b => requires_eq(a, b)
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_check_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_check_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Types,
            EnableDetailedProfiling = true,
            PreviousTraitCheckSnapshot = first.TraitCheckSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.TraitCheckSnapshot);
        Assert.NotNull(second.TraitCheckSnapshot);
        Assert.True(first.TraitCheckSnapshot!.Entries.Count > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.traitCheckPreviousCache.hits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.traitCheckPreviousCache.restoreHits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.traitCheckPreviousCache.validatedHits") > 0);
    }

    [Fact]
    public void CompilationPipeline_ImplOverlapSnapshot_ValidatesPreviousRegistrationQueries()
    {
        const string source = """
Eq :: trait
{
    eq :: Self -> Self -> Bool;
}

Box :: type
{
    Box(Int)
}

@impl(Eq)
eq :: Box -> Box -> Bool
{
    _ => _ => true
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "impl_overlap_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Namer,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "impl_overlap_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Namer,
            EnableDetailedProfiling = true,
            PreviousImplOverlapCheckSnapshot = first.ImplOverlapCheckSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.ImplOverlapCheckSnapshot);
        Assert.NotNull(second.ImplOverlapCheckSnapshot);
        Assert.True(first.ImplOverlapCheckSnapshot!.Entries.Count > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.implOverlapPreviousSnapshot.restoreHits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.implOverlapPreviousSnapshot.hits") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.implOverlapPreviousSnapshot.validatedHits") > 0);
    }

    [Fact]
    public void CompilationPipeline_ImplOverlapSnapshot_RestoresPreviousConflictCandidate()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person(String)
}

PersonAlias :: type = Person;

@impl(Show)
show :: Person -> String
{
    p => "person"
}

@impl(Show)
show :: PersonAlias -> String
{
    p => "alias"
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "impl_overlap_snapshot_conflict_restore.eidos",
            StopAtPhase = CompilationPhase.Namer,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "impl_overlap_snapshot_conflict_restore.eidos",
            StopAtPhase = CompilationPhase.Namer,
            EnableDetailedProfiling = true,
            PreviousImplOverlapCheckSnapshot = first.ImplOverlapCheckSnapshot,
            UseColors = false
        }).Run();

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.NotNull(first.ImplOverlapCheckSnapshot);
        Assert.NotNull(second.ImplOverlapCheckSnapshot);
        var debugInfo = string.Join(
            Environment.NewLine,
            [
                $"first: {string.Join(" || ", first.ImplOverlapCheckSnapshot!.Entries.Select(entry => $"{entry.QueryKey} :: {entry.CandidateSetFingerprint} :: {entry.ConflictingImplKey}"))}",
                $"second: {string.Join(" || ", second.ImplOverlapCheckSnapshot!.Entries.Select(entry => $"{entry.QueryKey} :: {entry.CandidateSetFingerprint} :: {entry.ConflictingImplKey}"))}",
                $"counters: {string.Join(", ", second.ProfilingCounters.OrderBy(pair => pair.Key).Where(pair => pair.Key.Contains("implOverlap", StringComparison.Ordinal)).Select(pair => $"{pair.Key}={pair.Value}"))}"
            ]);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.implOverlapPreviousSnapshot.conflictRestoreHits") > 0, debugInfo);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.implOverlapPreviousSnapshot.validatedHits") > 0, debugInfo);
        var diagnostic = Assert.Single(
            second.Diagnostics,
            item => item.Code == "E3004" &&
                    item.Message.Contains("Ambiguous overlapping impl registration", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requested impl head: @impl(Show) on PersonAlias", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("existing impl head: @impl(Show) on Person", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ImplOverlapSnapshot_UsesStableKeysAcrossSymbolIdChurn()
    {
        const string firstSource = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person(String)
}

PersonAlias :: type = Person;

@impl(Show)
show :: Person -> String
{
    p => "person"
}

@impl(Show)
show :: PersonAlias -> String
{
    p => "alias"
}
""";

        const string secondSource = """
Unused :: type {
    Unused
}

Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person(String)
}

PersonAlias :: type = Person;

@impl(Show)
show :: Person -> String
{
    p => "person"
}

@impl(Show)
show :: PersonAlias -> String
{
    p => "alias"
}
""";

        var first = new CompilationPipeline(firstSource, new CompilationOptions
        {
            InputFile = "impl_overlap_snapshot_stable_key.eidos",
            StopAtPhase = CompilationPhase.Namer,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "impl_overlap_snapshot_stable_key.eidos",
            StopAtPhase = CompilationPhase.Namer,
            EnableDetailedProfiling = true,
            PreviousImplOverlapCheckSnapshot = first.ImplOverlapCheckSnapshot,
            UseColors = false
        }).Run();

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.NotNull(first.ImplOverlapCheckSnapshot);
        Assert.NotNull(second.ImplOverlapCheckSnapshot);
        var debugInfo = string.Join(
            Environment.NewLine,
            [
                $"first: {string.Join(" || ", first.ImplOverlapCheckSnapshot!.Entries.Select(entry => $"{entry.QueryKey} :: {entry.CandidateSetFingerprint} :: {entry.ConflictingImplKey}"))}",
                $"second: {string.Join(" || ", second.ImplOverlapCheckSnapshot!.Entries.Select(entry => $"{entry.QueryKey} :: {entry.CandidateSetFingerprint} :: {entry.ConflictingImplKey}"))}",
                $"counters: {string.Join(", ", second.ProfilingCounters.OrderBy(pair => pair.Key).Where(pair => pair.Key.Contains("implOverlap", StringComparison.Ordinal)).Select(pair => $"{pair.Key}={pair.Value}"))}"
            ]);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.implOverlapPreviousSnapshot.conflictRestoreHits") > 0, debugInfo);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.implOverlapPreviousSnapshot.validatedHits") > 0, debugInfo);
    }

}
