using Eidosc.Pipeline;
using Eidosc.Semantic;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ProjectModuleDependencySignatureSnapshotTests
{
    [Fact]
    public void Create_CombinesSemanticTypedMemberAndMirDependencyHashes()
    {
        var graph = CreateGraph();
        var semantic = new ProjectModuleSemanticSignatureSnapshot(
            ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleSemanticSignatureNode("Lib", [], [], "surface-lib", "semantic-deps-lib", "semantic-lib"),
                new ProjectModuleSemanticSignatureNode("Main", ["Lib"], [], "surface-main", "semantic-deps-main", "semantic-main")
            ]);
        var typed = new ProjectModuleTypedSemanticSnapshot(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleTypedSemanticNode("Lib", [], [], "typed-surface-lib", "typed-deps-lib", "typed-lib"),
                new ProjectModuleTypedSemanticNode("Main", ["Lib"], [], "typed-surface-main", "typed-deps-main", "typed-main")
            ]);
        var member = new ProjectModuleMemberIndexSnapshot(
            ProjectModuleMemberIndexSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleMemberIndexNode("Lib", "Lib", false, "member-local-lib", "member-deps-lib", "member-lib", [], [], []),
                new ProjectModuleMemberIndexNode("Main", "Main", false, "member-local-main", "member-deps-main", "member-main", [], [], [])
            ]);
        var mir = new ProjectModuleMirArtifactSnapshot(
            ProjectModuleMirArtifactSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleMirArtifactNode("Lib", [], "typed-lib", "mir-functions-lib", "mir-lib"),
                new ProjectModuleMirArtifactNode("Main", ["Lib"], "typed-main", "mir-functions-main", "mir-main")
            ]);

        var snapshot = ProjectModuleDependencySignatureSnapshot.Create(graph, semantic, typed, member, mir);

        var main = Assert.Single(snapshot.Nodes, static node => node.ModuleKey == "Main");
        Assert.Equal(["Lib"], main.Dependencies);
        Assert.False(main.SourceAvailable);
        Assert.Equal("", main.SourceHash);
        Assert.Equal("", main.InputSignatureHash);
        Assert.Equal("semantic-deps-main", main.SemanticDependencySignatureHash);
        Assert.Equal("typed-deps-main", main.TypedDependencySignatureHash);
        Assert.Equal("member-deps-main", main.MemberDependencySignatureHash);
        Assert.Equal("typed-main", main.MirDependencySignatureHash);
        Assert.Equal("", main.GlobalCoherenceHash);
        Assert.True(main.SemanticAvailable);
        Assert.True(main.TypedAvailable);
        Assert.True(main.MemberIndexAvailable);
        Assert.True(main.MirAvailable);
        Assert.NotEmpty(main.CombinedDependencySignatureHash);
    }

    [Fact]
    public void Create_IncludesGlobalCoherenceHashWhenImplOverlapSnapshotIsAvailable()
    {
        var graph = CreateGraph();
        var first = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            Typed("typed-deps-main"),
            Member("member-deps-main"),
            Mir("typed-main"),
            graphSignatures: GraphSignatures("graph-deps-lib", "graph-deps-main"),
            implOverlapChecks: ImplOverlap("candidates-v1"));
        var second = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            Typed("typed-deps-main"),
            Member("member-deps-main"),
            Mir("typed-main"),
            graphSignatures: GraphSignatures("graph-deps-lib", "graph-deps-main"),
            implOverlapChecks: ImplOverlap("candidates-v2"));

        Assert.NotEmpty(Node(first, "Main").GlobalCoherenceHash);
        Assert.NotEqual(Node(first, "Main").GlobalCoherenceHash, Node(second, "Main").GlobalCoherenceHash);
        Assert.True(Node(second, "Main").IsCompatibleWith(
            Node(first, "Main"),
            ProjectModuleDependencySignatureRequirement.SemanticOnly));
        Assert.False(Node(second, "Main").IsCompatibleWith(
            Node(first, "Main"),
            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir));
    }

    [Fact]
    public void Create_ChangesCombinedHashWhenDependencySignatureChanges()
    {
        var graph = CreateGraph();
        var first = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            typed: null,
            memberIndex: null,
            mir: null);
        var second = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main-changed"),
            typed: null,
            memberIndex: null,
            mir: null);

        Assert.NotEqual(
            Node(first, "Main").CombinedDependencySignatureHash,
            Node(second, "Main").CombinedDependencySignatureHash);
    }

    [Fact]
    public void Create_UsesGraphSignatureDependencyHashWhenAvailable()
    {
        var graph = CreateGraph();
        var first = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            typed: null,
            memberIndex: null,
            mir: null,
            GraphSignatures("graph-deps-lib-v1", "graph-deps-main-v1"));
        var second = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            typed: null,
            memberIndex: null,
            mir: null,
            GraphSignatures("graph-deps-lib-v2", "graph-deps-main-v2"));

        Assert.Equal("graph-deps-main-v1", Node(first, "Main").GraphDependencySignatureHash);
        Assert.False(Node(second, "Main").IsCompatibleWith(
            Node(first, "Main"),
            ProjectModuleDependencySignatureRequirement.SemanticOnly));
    }

    [Fact]
    public void IsCompatibleWith_RejectsBodyOnlySourceChange()
    {
        var graph = CreateGraph();
        var first = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            Typed("typed-deps-main"),
            Member("member-deps-main"),
            Mir("typed-main"),
            GraphSignatures("graph-deps-lib", "graph-deps-main", mainSourceHash: "source-main-v1"));
        var second = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            Typed("typed-deps-main"),
            Member("member-deps-main"),
            Mir("typed-main"),
            GraphSignatures("graph-deps-lib", "graph-deps-main", mainSourceHash: "source-main-v2"));

        Assert.False(Node(second, "Main").IsCompatibleWith(
            Node(first, "Main"),
            ProjectModuleDependencySignatureRequirement.SemanticOnly));
        Assert.False(Node(second, "Main").IsCompatibleWith(
            Node(first, "Main"),
            ProjectModuleDependencySignatureRequirement.SemanticTyped));
        Assert.False(Node(second, "Main").IsCompatibleWith(
            Node(first, "Main"),
            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir));
    }

    [Fact]
    public void IsCompatibleWith_RejectsLocalCompilerFlagChange()
    {
        var graph = CreateGraph();
        var first = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            Typed("typed-deps-main"),
            Member("member-deps-main"),
            Mir("typed-main"),
            GraphSignatures("graph-deps-lib", "graph-deps-main", mainSignatureHash: "input-main-v1"));
        var second = ProjectModuleDependencySignatureSnapshot.Create(
            graph,
            Semantic("semantic-deps-main"),
            Typed("typed-deps-main"),
            Member("member-deps-main"),
            Mir("typed-main"),
            GraphSignatures("graph-deps-lib", "graph-deps-main", mainSignatureHash: "input-main-v2"));

        Assert.False(Node(second, "Main").IsCompatibleWith(
            Node(first, "Main"),
            ProjectModuleDependencySignatureRequirement.SemanticOnly));
    }

    [Fact]
    public void IsCompatibleWith_AllowsSemanticOnlyWithoutTypedLayers()
    {
        var current = Node(ProjectModuleDependencySignatureSnapshot.Create(
            CreateGraph(),
            Semantic("semantic-deps-main"),
            typed: null,
            memberIndex: null,
            mir: null,
            GraphSignatures("graph-deps-lib", "graph-deps-main")), "Main");
        var previous = Node(ProjectModuleDependencySignatureSnapshot.Create(
            CreateGraph(),
            Semantic("semantic-deps-main"),
            typed: null,
            memberIndex: null,
            mir: null,
            GraphSignatures("graph-deps-lib", "graph-deps-main")), "Main");

        Assert.True(current.IsCompatibleWith(previous, ProjectModuleDependencySignatureRequirement.SemanticOnly));
        Assert.False(current.IsCompatibleWith(previous, ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir));
    }

    [Fact]
    public void Create_MarksUnavailableLayersWithoutInventingHashes()
    {
        var snapshot = ProjectModuleDependencySignatureSnapshot.Create(
            CreateGraph(),
            semantic: null,
            typed: null,
            memberIndex: null,
            mir: null);

        var main = Node(snapshot, "Main");
        Assert.False(main.SemanticAvailable);
        Assert.False(main.SourceAvailable);
        Assert.False(main.TypedAvailable);
        Assert.False(main.MemberIndexAvailable);
        Assert.False(main.MirAvailable);
        Assert.Equal("", main.SemanticDependencySignatureHash);
        Assert.Equal("", main.TypedDependencySignatureHash);
        Assert.Equal("", main.MemberDependencySignatureHash);
        Assert.Equal("", main.MirDependencySignatureHash);
    }

    private static ProjectModuleGraphSnapshot CreateGraph()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("lib.eidos", "Lib");
        graph.RegisterModuleIdentity("main.eidos", "Main");
        graph.AddDependency("Main", "Lib");
        return ProjectModuleGraphSnapshot.FromDependencyGraph(graph);
    }

    private static ProjectModuleSemanticSignatureSnapshot Semantic(string mainDependencyHash) =>
        new(
            ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleSemanticSignatureNode("Lib", [], [], "surface-lib", "semantic-deps-lib", "semantic-lib"),
                new ProjectModuleSemanticSignatureNode("Main", ["Lib"], [], "surface-main", mainDependencyHash, "semantic-main")
            ]);

    private static ProjectModuleTypedSemanticSnapshot Typed(string mainDependencyHash) =>
        new(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleTypedSemanticNode("Lib", [], [], "typed-surface-lib", "typed-deps-lib", "typed-lib"),
                new ProjectModuleTypedSemanticNode("Main", ["Lib"], [], "typed-surface-main", mainDependencyHash, "typed-main")
            ]);

    private static ProjectModuleMemberIndexSnapshot Member(string mainDependencyHash) =>
        new(
            ProjectModuleMemberIndexSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleMemberIndexNode("Lib", "Lib", false, "member-local-lib", "member-deps-lib", "member-lib", [], [], []),
                new ProjectModuleMemberIndexNode("Main", "Main", false, "member-local-main", mainDependencyHash, "member-main", [], [], [])
            ]);

    private static ProjectModuleMirArtifactSnapshot Mir(string mainDependencyHash) =>
        new(
            ProjectModuleMirArtifactSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleMirArtifactNode("Lib", [], "typed-lib", "mir-functions-lib", "mir-lib"),
                new ProjectModuleMirArtifactNode("Main", ["Lib"], mainDependencyHash, "mir-functions-main", "mir-main")
            ]);

    private static ImplOverlapCheckSnapshot ImplOverlap(string candidateFingerprint) =>
        new(
            ImplOverlapCheckSnapshot.CurrentSchemaVersion,
            [
                new ImplOverlapCheckSnapshotEntry(
                    "query",
                    "trait",
                    "Type",
                    "[]",
                    "[]",
                    CandidateCount: 1,
                    CandidateSetFingerprint: candidateFingerprint,
                    NonOverlappingCandidateCount: 1,
                    SpecializationAllowedCandidateCount: 0,
                    HasConflict: false,
                    ConflictingImplKey: null,
                    SpecializationRelation: null)
            ]);

    private static ProjectModuleSignatureSnapshot GraphSignatures(
        string libSignatureHash,
        string mainDependencyHash,
        string mainSourceHash = "source-main",
        string mainSignatureHash = "graph-main") =>
        new(
            [
                new ProjectModuleSignatureNode(
                    "Lib",
                    ["lib.eidos"],
                    [],
                    "source-lib",
                    "",
                    libSignatureHash),
                new ProjectModuleSignatureNode(
                    "Main",
                    ["main.eidos"],
                    ["Lib"],
                    mainSourceHash,
                    mainDependencyHash,
                    mainSignatureHash)
            ]);

    private static ProjectModuleDependencySignatureNode Node(
        ProjectModuleDependencySignatureSnapshot snapshot,
        string moduleKey) =>
        Assert.Single(snapshot.Nodes, node => node.ModuleKey == moduleKey);
}
