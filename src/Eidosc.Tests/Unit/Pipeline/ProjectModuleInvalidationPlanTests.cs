using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ProjectModuleInvalidationPlanTests
{
    [Fact]
    public void FromSemanticSignatures_WithoutPreviousMarksAllCurrentModulesAffected()
    {
        var current = Snapshot(
            Node("A", exportHash: "a", dependencyHash: "none", semanticHash: "sa"),
            Node("B", exportHash: "b", dependencyHash: "sa", semanticHash: "sb", dependencies: ["A"]));

        var plan = ProjectModuleInvalidationPlan.FromSemanticSignatures(null, current);

        Assert.Equal(["A", "B"], plan.AffectedModules);
        Assert.Empty(plan.UnchangedModules);
        Assert.All(plan.Changes, static change => Assert.Equal(ProjectModuleInvalidationReason.Added, change.Reason));
    }

    [Fact]
    public void FromSemanticSignatures_LeavesPrivateImplementationOnlyChangesUnchanged()
    {
        var before = Snapshot(Node("A", exportHash: "api", dependencyHash: "none", semanticHash: "s1"));
        var after = Snapshot(Node("A", exportHash: "api", dependencyHash: "none", semanticHash: "s1"));

        var plan = ProjectModuleInvalidationPlan.FromSemanticSignatures(before, after);

        Assert.Empty(plan.Changes);
        Assert.Empty(plan.AffectedModules);
        Assert.Equal(["A"], plan.UnchangedModules);
    }

    [Fact]
    public void FromSemanticSignatures_PropagatesExportSurfaceChangesToDependents()
    {
        var before = Snapshot(
            Node("A", exportHash: "api1", dependencyHash: "none", semanticHash: "sa1"),
            Node("B", exportHash: "b", dependencyHash: "sa1", semanticHash: "sb1", dependencies: ["A"]),
            Node("C", exportHash: "c", dependencyHash: "sb1", semanticHash: "sc1", dependencies: ["B"]));
        var after = Snapshot(
            Node("A", exportHash: "api2", dependencyHash: "none", semanticHash: "sa2"),
            Node("B", exportHash: "b", dependencyHash: "sa2", semanticHash: "sb2", dependencies: ["A"]),
            Node("C", exportHash: "c", dependencyHash: "sb2", semanticHash: "sc2", dependencies: ["B"]));

        var plan = ProjectModuleInvalidationPlan.FromSemanticSignatures(before, after);

        Assert.Contains(plan.Changes, static change =>
            change.ModuleKey == "A" &&
            change.Reason == ProjectModuleInvalidationReason.ExportSurfaceChanged);
        Assert.Equal(["A", "B", "C"], plan.AffectedModules);
        Assert.Empty(plan.UnchangedModules);
    }

    [Fact]
    public void FromSemanticSignatures_RemovedModuleInvalidatesPreviousDependents()
    {
        var before = Snapshot(
            Node("A", exportHash: "a", dependencyHash: "none", semanticHash: "sa"),
            Node("B", exportHash: "b", dependencyHash: "sa", semanticHash: "sb", dependencies: ["A"]));
        var after = Snapshot(Node("B", exportHash: "b", dependencyHash: "missing", semanticHash: "sb2", dependencies: ["A"]));

        var plan = ProjectModuleInvalidationPlan.FromSemanticSignatures(before, after);

        Assert.Contains(plan.Changes, static change =>
            change.ModuleKey == "A" &&
            change.Reason == ProjectModuleInvalidationReason.Removed);
        Assert.Equal(["B"], plan.AffectedModules);
        Assert.Empty(plan.UnchangedModules);
    }

    [Fact]
    public void FromTypedSemanticSignatures_WithoutPreviousMarksAllCurrentModulesAffected()
    {
        var current = TypedSnapshot(
            TypedNode("A", localHash: "a", dependencyHash: "none", typedHash: "ta"),
            TypedNode("B", localHash: "b", dependencyHash: "ta", typedHash: "tb", dependencies: ["A"]));

        var plan = ProjectModuleInvalidationPlan.FromTypedSemanticSignatures(null, current);

        Assert.Equal(["A", "B"], plan.AffectedModules);
        Assert.Empty(plan.UnchangedModules);
        Assert.All(plan.Changes, static change => Assert.Equal(ProjectModuleInvalidationReason.Added, change.Reason));
    }

    [Fact]
    public void FromTypedSemanticSignatures_LeavesStableTypedSurfaceUnchanged()
    {
        var before = TypedSnapshot(TypedNode("A", localHash: "typed-api", dependencyHash: "none", typedHash: "t1"));
        var after = TypedSnapshot(TypedNode("A", localHash: "typed-api", dependencyHash: "none", typedHash: "t1"));

        var plan = ProjectModuleInvalidationPlan.FromTypedSemanticSignatures(before, after);

        Assert.Empty(plan.Changes);
        Assert.Empty(plan.AffectedModules);
        Assert.Equal(["A"], plan.UnchangedModules);
    }

    [Fact]
    public void FromTypedSemanticSignatures_PropagatesTypedSurfaceChangesToDependents()
    {
        var before = TypedSnapshot(
            TypedNode("A", localHash: "typed1", dependencyHash: "none", typedHash: "ta1"),
            TypedNode("B", localHash: "b", dependencyHash: "ta1", typedHash: "tb1", dependencies: ["A"]));
        var after = TypedSnapshot(
            TypedNode("A", localHash: "typed2", dependencyHash: "none", typedHash: "ta2"),
            TypedNode("B", localHash: "b", dependencyHash: "ta2", typedHash: "tb2", dependencies: ["A"]));

        var plan = ProjectModuleInvalidationPlan.FromTypedSemanticSignatures(before, after);

        Assert.Contains(plan.Changes, static change =>
            change.ModuleKey == "A" &&
            change.Reason == ProjectModuleInvalidationReason.TypedSurfaceChanged);
        Assert.Equal(["A", "B"], plan.AffectedModules);
        Assert.Empty(plan.UnchangedModules);
    }

    private static ProjectModuleSemanticSignatureSnapshot Snapshot(params ProjectModuleSemanticSignatureNode[] nodes) =>
        new(ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion, nodes);

    private static ProjectModuleTypedSemanticSnapshot TypedSnapshot(params ProjectModuleTypedSemanticNode[] nodes) =>
        new(ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion, nodes);

    private static ProjectModuleSemanticSignatureNode Node(
        string moduleKey,
        string exportHash,
        string dependencyHash,
        string semanticHash,
        IReadOnlyList<string>? dependencies = null) =>
        new(
            moduleKey,
            dependencies ?? [],
            [],
            exportHash,
            dependencyHash,
            semanticHash);

    private static ProjectModuleTypedSemanticNode TypedNode(
        string moduleKey,
        string localHash,
        string dependencyHash,
        string typedHash,
        IReadOnlyList<string>? dependencies = null) =>
        new(
            moduleKey,
            dependencies ?? [],
            [],
            localHash,
            dependencyHash,
            typedHash);
}
