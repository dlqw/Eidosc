using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ProjectModuleExecutionPlanTests
{
    [Fact]
    public void FromSchedule_MarksAffectedModulesCompileAndUnchangedModulesRestoreByLayer()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("a.eidos", "A");
        graph.RegisterModuleIdentity("b.eidos", "B");
        graph.RegisterModuleIdentity("c.eidos", "C");
        graph.RegisterModuleIdentity("d.eidos", "D");
        graph.AddDependency("B", "A");
        graph.AddDependency("C", "A");
        graph.AddDependency("D", "B");
        graph.AddDependency("D", "C");
        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(ProjectModuleGraphSnapshot.FromDependencyGraph(graph));
        var invalidation = new ProjectModuleInvalidationPlan(
            [new ProjectModuleInvalidationChange("B", ProjectModuleInvalidationReason.ExportSurfaceChanged)],
            ["B", "D"],
            ["A", "C"]);

        var plan = ProjectModuleExecutionPlan.FromSchedule(schedule, invalidation);

        Assert.Equal(4, plan.TotalModules);
        Assert.Equal(2, plan.CompileModules);
        Assert.Equal(2, plan.RestoreModules);
        Assert.Equal(1, plan.MaxCompileParallelWidth);
        Assert.Equal(1, plan.MaxRestoreParallelWidth);
        Assert.Equal(ProjectModuleExecutionAction.Restore, Assert.Single(plan.Layers[0].Modules).Action);
        Assert.Equal(
            [ProjectModuleExecutionAction.Compile, ProjectModuleExecutionAction.Restore],
            plan.Layers[1].Modules.Select(static item => item.Action));
        Assert.Equal(ProjectModuleExecutionAction.Compile, Assert.Single(plan.Layers[2].Modules).Action);
    }

    [Fact]
    public void FromSchedule_AllUnchangedModulesCanRestoreInParallel()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("a.eidos", "A");
        graph.RegisterModuleIdentity("b.eidos", "B");
        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(ProjectModuleGraphSnapshot.FromDependencyGraph(graph));
        var invalidation = new ProjectModuleInvalidationPlan([], [], ["A", "B"]);

        var plan = ProjectModuleExecutionPlan.FromSchedule(schedule, invalidation);

        Assert.Equal(0, plan.CompileModules);
        Assert.Equal(2, plan.RestoreModules);
        Assert.Equal(0, plan.MaxCompileParallelWidth);
        Assert.Equal(2, plan.MaxRestoreParallelWidth);
        Assert.All(plan.Layers[0].Modules, static item => Assert.Equal(ProjectModuleExecutionAction.Restore, item.Action));
    }

    [Fact]
    public void FromSchedule_MarksPrecompiledModulesAsReadyArtifacts()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("<precompiled:Std::Core>", "Std::Core");
        graph.RegisterModuleIdentity("main.eidos", "Main");
        graph.AddDependency("Main", "Std::Core");
        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(ProjectModuleGraphSnapshot.FromDependencyGraph(graph));
        var invalidation = new ProjectModuleInvalidationPlan(
            [
                new ProjectModuleInvalidationChange("Std::Core", ProjectModuleInvalidationReason.ExportSurfaceChanged),
                new ProjectModuleInvalidationChange("Main", ProjectModuleInvalidationReason.DependencySignatureChanged)
            ],
            ["Std::Core", "Main"],
            []);

        var plan = ProjectModuleExecutionPlan.FromSchedule(
            schedule,
            invalidation,
            ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);

        Assert.Equal(2, plan.TotalModules);
        Assert.Equal(1, plan.CompileModules);
        Assert.Equal(0, plan.RestoreModules);
        Assert.Equal(1, plan.ReadyArtifactModules);
        Assert.Equal(1, plan.MaxCompileParallelWidth);
        Assert.Equal(0, plan.MaxRestoreParallelWidth);
        Assert.Equal(1, plan.MaxReadyArtifactParallelWidth);
        Assert.Equal(ProjectModuleExecutionAction.ReadyArtifact, Assert.Single(plan.Layers[0].Modules).Action);
        Assert.Equal(ProjectModuleExecutionAction.Compile, Assert.Single(plan.Layers[1].Modules).Action);
    }

    [Fact]
    public void IsPrecompiledReadyArtifact_RecognizesStdlibPrecompiledSourcePath()
    {
        var item = new ProjectModuleBuildItem(
            0,
            "Std::Array",
            ["C:/repo/Eidosc/src/Eidosc/Stdlib/Precompiled/Std/Array.eidos"],
            [],
            []);

        Assert.True(ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact(item));
    }

    [Fact]
    public void ArtifactReadiness_UsesSnapshotHashesForRestoreModules()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("a.eidos", "A");
        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(ProjectModuleGraphSnapshot.FromDependencyGraph(graph));
        var invalidation = new ProjectModuleInvalidationPlan([], [], ["A"]);
        var execution = ProjectModuleExecutionPlan.FromSchedule(schedule, invalidation);
        var semantic = new ProjectModuleSemanticSignatureSnapshot(
            ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleSemanticSignatureNode(
                    "A",
                    [],
                    [],
                    "surface-a",
                    "deps-a",
                    "semantic-a")
            ]);
        var typed = new ProjectModuleTypedSemanticSnapshot(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleTypedSemanticNode(
                    "A",
                    [],
                    [],
                    "typed-surface-a",
                    "typed-deps-a",
                    "typed-a")
            ]);
        var mir = new ProjectModuleMirArtifactSnapshot(
            "module-mir-artifact-snapshot-v1",
            [
                new ProjectModuleMirArtifactNode(
                    "A",
                    [],
                    "typed-a",
                    "mir-functions-a",
                    "mir-a")
            ]);

        var plan = ProjectModuleArtifactReadinessPlan.FromExecutionPlan(
            execution,
            semantic,
            typed,
            mir,
            (moduleKey, kind, sourceHash, dependencyHash) =>
                moduleKey == "A" &&
                ((kind == ProjectModuleArtifactKinds.SemanticSignature &&
                  sourceHash == "surface-a" &&
                  dependencyHash == "deps-a") ||
                 (kind == ProjectModuleArtifactKinds.TypedSemanticSignature &&
                  sourceHash == "typed-surface-a" &&
                  dependencyHash == "typed-deps-a") ||
                 (kind == ProjectModuleArtifactKinds.MirArtifact &&
                  sourceHash == "typed-a" &&
                  dependencyHash == "mir-a")));

        Assert.Equal(1, plan.RestoreModules);
        Assert.Equal(1, plan.SemanticReadyModules);
        Assert.Equal(0, plan.SemanticMissingModules);
        Assert.Equal(1, plan.TypedSemanticReadyModules);
        Assert.Equal(0, plan.TypedSemanticMissingModules);
        Assert.Equal(1, plan.MirReadyModules);
        Assert.Equal(0, plan.MirMissingModules);
    }

    [Fact]
    public void ArtifactRestorePlan_RestoresOnlyWhenAllArtifactsReady()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("<precompiled:Std::Core>", "Std::Core");
        graph.RegisterModuleIdentity("a.eidos", "A");
        graph.RegisterModuleIdentity("b.eidos", "B");
        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(ProjectModuleGraphSnapshot.FromDependencyGraph(graph));
        var invalidation = new ProjectModuleInvalidationPlan([], [], ["Std::Core", "A", "B"]);
        var execution = ProjectModuleExecutionPlan.FromSchedule(
            schedule,
            invalidation,
            ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);
        var readiness = new ProjectModuleArtifactReadinessPlan(
            [
                new ProjectModuleArtifactReadinessItem(
                    "Std::Core",
                    ProjectModuleExecutionAction.ReadyArtifact,
                    SemanticReady: true,
                    TypedSemanticReady: true,
                    MirReady: true),
                new ProjectModuleArtifactReadinessItem(
                    "A",
                    ProjectModuleExecutionAction.Restore,
                    SemanticReady: true,
                    TypedSemanticReady: true,
                    MirReady: true),
                new ProjectModuleArtifactReadinessItem(
                    "B",
                    ProjectModuleExecutionAction.Restore,
                    SemanticReady: true,
                    TypedSemanticReady: true,
                    MirReady: false)
            ],
            TotalModules: 3,
            CompileModules: 0,
            RestoreModules: 2,
            ReadyArtifactModules: 1,
            SemanticReadyModules: 3,
            SemanticMissingModules: 0,
            TypedSemanticReadyModules: 3,
            TypedSemanticMissingModules: 0,
            MirReadyModules: 2,
            MirMissingModules: 1);

        var restore = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(execution, readiness);

        Assert.Equal(3, restore.TotalModules);
        Assert.Equal(1, restore.RestoreModules);
        Assert.Equal(1, restore.BlockedModules);
        Assert.Equal(1, restore.ReadyArtifactModules);
        Assert.Equal(0, restore.CompileModules);
        Assert.Contains(restore.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleArtifactRestoreAction.Restore);
        Assert.Contains(restore.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "B" && item.Action == ProjectModuleArtifactRestoreAction.Blocked);
        Assert.Contains(restore.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "Std::Core" && item.Action == ProjectModuleArtifactRestoreAction.ReadyArtifact);
    }

    [Fact]
    public void ArtifactRestorePlan_WithSemanticOnlyRequirement_DoesNotRequireTypedOrMirArtifacts()
    {
        var execution = new ProjectModuleExecutionPlan(
            [
                new ProjectModuleExecutionLayer(
                    0,
                    [new ProjectModuleExecutionItem("A", ProjectModuleExecutionAction.Restore, [], [], [])],
                    CompileCount: 0,
                    RestoreCount: 1,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 1,
            CompileModules: 0,
            RestoreModules: 1,
            ReadyArtifactModules: 0,
            MaxCompileParallelWidth: 0,
            MaxRestoreParallelWidth: 1,
            MaxReadyArtifactParallelWidth: 0);
        var readiness = new ProjectModuleArtifactReadinessPlan(
            [
                new ProjectModuleArtifactReadinessItem(
                    "A",
                    ProjectModuleExecutionAction.Restore,
                    SemanticReady: true,
                    TypedSemanticReady: false,
                    MirReady: false)
            ],
            TotalModules: 1,
            CompileModules: 0,
            RestoreModules: 1,
            ReadyArtifactModules: 0,
            SemanticReadyModules: 1,
            SemanticMissingModules: 0,
            TypedSemanticReadyModules: 0,
            TypedSemanticMissingModules: 0,
            MirReadyModules: 0,
            MirMissingModules: 0);

        var restore = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
            execution,
            readiness,
            ProjectModuleArtifactRequirement.SemanticOnly);

        Assert.Equal(1, restore.RestoreModules);
        Assert.Equal(0, restore.BlockedModules);
        Assert.Contains(restore.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleArtifactRestoreAction.Restore);
    }

    [Fact]
    public void ArtifactRestorePlan_DoesNotCountCompileModulesAsBlocked()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("a.eidos", "A");
        graph.RegisterModuleIdentity("b.eidos", "B");
        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(ProjectModuleGraphSnapshot.FromDependencyGraph(graph));
        var invalidation = new ProjectModuleInvalidationPlan(
            [new ProjectModuleInvalidationChange("A", ProjectModuleInvalidationReason.ExportSurfaceChanged)],
            ["A"],
            ["B"]);
        var execution = ProjectModuleExecutionPlan.FromSchedule(schedule, invalidation);
        var readiness = new ProjectModuleArtifactReadinessPlan(
            [
                new ProjectModuleArtifactReadinessItem(
                    "A",
                    ProjectModuleExecutionAction.Compile,
                    SemanticReady: false,
                    TypedSemanticReady: false,
                    MirReady: false),
                new ProjectModuleArtifactReadinessItem(
                    "B",
                    ProjectModuleExecutionAction.Restore,
                    SemanticReady: true,
                    TypedSemanticReady: true,
                    MirReady: true)
            ],
            TotalModules: 2,
            CompileModules: 1,
            RestoreModules: 1,
            ReadyArtifactModules: 0,
            SemanticReadyModules: 1,
            SemanticMissingModules: 0,
            TypedSemanticReadyModules: 1,
            TypedSemanticMissingModules: 0,
            MirReadyModules: 1,
            MirMissingModules: 0);

        var restore = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(execution, readiness);
        var snapshot = ProjectModuleArtifactRestoreExecutionSnapshot.FromRestorePlan(restore);

        Assert.Equal(1, restore.CompileModules);
        Assert.Equal(1, restore.RestoreModules);
        Assert.Equal(0, restore.BlockedModules);
        Assert.Equal(1, snapshot.CompiledModules);
        Assert.Equal(1, snapshot.RestoredModules);
        Assert.Equal(0, snapshot.BlockedModules);
    }

    [Fact]
    public void ArtifactRestorePlan_GateWithPayload_CompilesUnvalidatedRestoreModules()
    {
        var readiness = new ProjectModuleArtifactReadinessPlan(
            [
                new ProjectModuleArtifactReadinessItem(
                    "A",
                    ProjectModuleExecutionAction.Restore,
                    SemanticReady: true,
                    TypedSemanticReady: true,
                    MirReady: true),
                new ProjectModuleArtifactReadinessItem(
                    "B",
                    ProjectModuleExecutionAction.Restore,
                    SemanticReady: true,
                    TypedSemanticReady: true,
                    MirReady: true)
            ],
            TotalModules: 2,
            CompileModules: 0,
            RestoreModules: 2,
            ReadyArtifactModules: 0,
            SemanticReadyModules: 2,
            SemanticMissingModules: 0,
            TypedSemanticReadyModules: 2,
            TypedSemanticMissingModules: 0,
            MirReadyModules: 2,
            MirMissingModules: 0);
        var execution = new ProjectModuleExecutionPlan(
            [
                new ProjectModuleExecutionLayer(
                    0,
                    [
                        new ProjectModuleExecutionItem("A", ProjectModuleExecutionAction.Restore, [], [], []),
                        new ProjectModuleExecutionItem("B", ProjectModuleExecutionAction.Restore, [], [], [])
                    ],
                    CompileCount: 0,
                    RestoreCount: 2,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 2,
            CompileModules: 0,
            RestoreModules: 2,
            ReadyArtifactModules: 0,
            MaxCompileParallelWidth: 0,
            MaxRestoreParallelWidth: 2,
            MaxReadyArtifactParallelWidth: 0);
        var restore = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(execution, readiness);
        var payload = new ProjectModuleArtifactRestorePayloadSnapshot(
            "module-artifact-restore-payload-v1",
            [
                new ProjectModuleArtifactRestorePayloadItem(
                    "A",
                    Semantic: new ProjectModuleSemanticSignatureNode("A", [], [], "surface-a", "deps-a", "semantic-a"),
                    TypedSemantic: new ProjectModuleTypedSemanticNode(
                        "A",
                        [],
                        [],
                        "typed-surface-a",
                        "typed-deps-a",
                        "typed-a"),
                    Mir: new ProjectModuleMirArtifactNode("A", [], "typed-a", "mir-functions-a", "mir-a"),
                    SemanticLoaded: true,
                    TypedSemanticLoaded: true,
                    MirLoaded: true,
                    SemanticHashMatches: true,
                    TypedSemanticHashMatches: true,
                    MirHashMatches: true),
                new ProjectModuleArtifactRestorePayloadItem(
                    "B",
                    Semantic: new ProjectModuleSemanticSignatureNode("B", [], [], "surface-b", "deps-b", "semantic-b"),
                    TypedSemantic: new ProjectModuleTypedSemanticNode(
                        "B",
                        [],
                        [],
                        "typed-surface-b",
                        "typed-deps-b",
                        "typed-b"),
                    Mir: null,
                    SemanticLoaded: true,
                    TypedSemanticLoaded: true,
                    MirLoaded: false,
                    SemanticHashMatches: true,
                    TypedSemanticHashMatches: true,
                    MirHashMatches: false)
            ],
            RestoreModules: 2,
            LoadedModules: 1,
            ValidatedModules: 1,
            StaleModules: 0,
            MissingModules: 1,
            FailedModules: 0);

        var gated = restore.GateWithPayload(payload);

        Assert.Equal(1, gated.RestoreModules);
        Assert.Equal(0, gated.BlockedModules);
        Assert.Equal(1, gated.CompileModules);
        Assert.Contains(gated.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleArtifactRestoreAction.Restore);
        Assert.Contains(gated.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "B" && item.Action == ProjectModuleArtifactRestoreAction.Compile);
    }

    [Fact]
    public void ArtifactRestoreExecutor_GatesExecutionWithPayload()
    {
        var plan = new ProjectModuleArtifactRestorePlan(
            ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
            [
                new ProjectModuleArtifactRestoreLayer(
                    0,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "A",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true),
                        new ProjectModuleArtifactRestoreItem(
                            "B",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true)
                    ],
                    RestoreCount: 2,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 2,
            RestoreModules: 2,
            BlockedModules: 0,
            ReadyArtifactModules: 0,
            MaxRestoreParallelWidth: 2);
        var payload = new ProjectModuleArtifactRestorePayloadSnapshot(
            "module-artifact-restore-payload-v1",
            [
                new ProjectModuleArtifactRestorePayloadItem(
                    "A",
                    Semantic: null,
                    TypedSemantic: null,
                    Mir: null,
                    SemanticLoaded: false,
                    TypedSemanticLoaded: false,
                    MirLoaded: false,
                    SemanticHashMatches: false,
                    TypedSemanticHashMatches: false,
                    MirHashMatches: false),
                new ProjectModuleArtifactRestorePayloadItem(
                    "B",
                    Semantic: new ProjectModuleSemanticSignatureNode("B", [], [], "surface-b", "deps-b", "semantic-b"),
                    TypedSemantic: new ProjectModuleTypedSemanticNode("B", [], [], "typed-surface-b", "typed-deps-b", "typed-b"),
                    Mir: new ProjectModuleMirArtifactNode("B", [], "typed-b", "mir-functions-b", "mir-b"),
                    SemanticLoaded: true,
                    TypedSemanticLoaded: true,
                    MirLoaded: true,
                    SemanticHashMatches: true,
                    TypedSemanticHashMatches: true,
                    MirHashMatches: true)
            ],
            RestoreModules: 2,
            LoadedModules: 1,
            ValidatedModules: 1,
            StaleModules: 0,
            MissingModules: 1,
            FailedModules: 0);

        var execution = ProjectModuleArtifactRestoreExecutor.Execute(plan, payload);

        Assert.Equal(1, execution.RestoredModules);
        Assert.Equal(0, execution.BlockedModules);
        Assert.Equal(1, execution.CompiledModules);
        Assert.Contains(execution.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleArtifactRestoreExecutionAction.Compiled);
        Assert.Contains(execution.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "B" && item.Action == ProjectModuleArtifactRestoreExecutionAction.Restored);
    }

    [Fact]
    public void ArtifactRestorePlan_GateWithDependencySignatures_CompilesChangedDependencies()
    {
        var plan = SingleRestorePlan();
        var previous = DependencySignatures("semantic-deps-old", "typed-deps", "member-deps", "mir-deps");
        var current = DependencySignatures("semantic-deps-new", "typed-deps", "member-deps", "mir-deps");

        var gated = plan.GateWithDependencySignatures(
            current,
            previous,
            ProjectModuleDependencySignatureRequirement.SemanticOnly);

        Assert.Equal(0, gated.RestoreModules);
        Assert.Equal(0, gated.BlockedModules);
        Assert.Equal(1, gated.CompileModules);
        Assert.Contains(gated.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleArtifactRestoreAction.Compile);
    }

    [Fact]
    public void ArtifactRestoreExecutor_RequiresPayloadAndDependencySignatureValidation()
    {
        var plan = SingleRestorePlan();
        var payload = new ProjectModuleArtifactRestorePayloadSnapshot(
            "module-artifact-restore-payload-v1",
            [
                new ProjectModuleArtifactRestorePayloadItem(
                    "A",
                    Semantic: new ProjectModuleSemanticSignatureNode("A", [], [], "surface", "semantic-deps", "semantic"),
                    TypedSemantic: new ProjectModuleTypedSemanticNode("A", [], [], "typed-surface", "typed-deps", "typed"),
                    Mir: new ProjectModuleMirArtifactNode("A", [], "mir-deps", "mir-functions", "mir"),
                    SemanticLoaded: true,
                    TypedSemanticLoaded: true,
                    MirLoaded: true,
                    SemanticHashMatches: true,
                    TypedSemanticHashMatches: true,
                    MirHashMatches: true)
            ],
            RestoreModules: 1,
            LoadedModules: 1,
            ValidatedModules: 1,
            StaleModules: 0,
            MissingModules: 0,
            FailedModules: 0);
        var previous = DependencySignatures("semantic-deps", "typed-deps", "member-deps", "mir-deps-old");
        var current = DependencySignatures("semantic-deps", "typed-deps", "member-deps", "mir-deps-new");

        var execution = ProjectModuleArtifactRestoreExecutor.Execute(
            plan,
            payload,
            current,
            previous,
            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir);

        Assert.Equal(0, execution.RestoredModules);
        Assert.Equal(0, execution.BlockedModules);
        Assert.Equal(1, execution.CompiledModules);
        Assert.Contains(execution.Layers.SelectMany(static layer => layer.Modules), static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleArtifactRestoreExecutionAction.Compiled);
    }

    [Fact]
    public async Task ArtifactRestoreExecutor_ExecuteAsync_RunsRestoreAndCompileTasks()
    {
        var plan = new ProjectModuleArtifactRestorePlan(
            ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
            [
                new ProjectModuleArtifactRestoreLayer(
                    0,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "A",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true),
                        new ProjectModuleArtifactRestoreItem(
                            "B",
                            ProjectModuleArtifactRestoreAction.Compile,
                            SemanticReady: false,
                            TypedSemanticReady: false,
                            MirReady: false),
                        new ProjectModuleArtifactRestoreItem(
                            "C",
                            ProjectModuleArtifactRestoreAction.ReadyArtifact,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true),
                        new ProjectModuleArtifactRestoreItem(
                            "D",
                            ProjectModuleArtifactRestoreAction.Blocked,
                            SemanticReady: false,
                            TypedSemanticReady: false,
                            MirReady: false)
                    ],
                    RestoreCount: 1,
                    BlockedCount: 1,
                    ReadyArtifactCount: 1)
            ],
            TotalModules: 4,
            RestoreModules: 1,
            BlockedModules: 1,
            ReadyArtifactModules: 1,
            MaxRestoreParallelWidth: 1);
        var restored = new List<string>();
        var compiled = new List<string>();

        var execution = await ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
            plan,
            (item, _) =>
            {
                restored.Add(item.ModuleKey);
                return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
            },
            (item, _) =>
            {
                compiled.Add(item.ModuleKey);
                return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
            },
            maxDegreeOfParallelism: 2);

        Assert.True(execution.HasRealTaskExecution);
        Assert.Equal(["A"], restored);
        Assert.Equal(["B"], compiled);
        Assert.Equal(1, execution.RestoredModules);
        Assert.Equal(1, execution.CompiledModules);
        Assert.Equal(1, execution.ReadyArtifactModules);
        Assert.Equal(1, execution.BlockedModules);
        Assert.Equal(2, execution.SkippedModules);
        Assert.Contains(execution.Layers[0].Modules, static item =>
            item.ModuleKey == "C" &&
            item.Action == ProjectModuleArtifactRestoreExecutionAction.ReadyArtifact &&
            item.Status == ProjectModuleExecutionItemStatus.Skipped);
        Assert.Contains(execution.Layers[0].Modules, static item =>
            item.ModuleKey == "D" &&
            item.Action == ProjectModuleArtifactRestoreExecutionAction.Blocked &&
            item.Status == ProjectModuleExecutionItemStatus.Skipped);
    }

    [Fact]
    public async Task ArtifactRestoreExecutor_ExecuteAsync_RunsSameLayerRestoreTasksInParallelAndOrdersResults()
    {
        var plan = new ProjectModuleArtifactRestorePlan(
            ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
            [
                new ProjectModuleArtifactRestoreLayer(
                    0,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "B",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true),
                        new ProjectModuleArtifactRestoreItem(
                            "A",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true)
                    ],
                    RestoreCount: 2,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0),
                new ProjectModuleArtifactRestoreLayer(
                    1,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "C",
                            ProjectModuleArtifactRestoreAction.Compile,
                            SemanticReady: false,
                            TypedSemanticReady: false,
                            MirReady: false)
                    ],
                    RestoreCount: 0,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 3,
            RestoreModules: 2,
            BlockedModules: 0,
            ReadyArtifactModules: 0,
            MaxRestoreParallelWidth: 2);
        var layerZeroRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layerZeroStarted = 0;
        var layerZeroCompleted = 0;

        var task = ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
            plan,
            async (item, _) =>
            {
                Assert.True(item.ModuleKey is "A" or "B");
                Interlocked.Increment(ref layerZeroStarted);
                await layerZeroRelease.Task.ConfigureAwait(false);
                Interlocked.Increment(ref layerZeroCompleted);
                return ProjectModuleExecutionItemResult.Completed;
            },
            (item, _) =>
            {
                Assert.Equal("C", item.ModuleKey);
                Assert.Equal(2, Volatile.Read(ref layerZeroCompleted));
                return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
            },
            maxDegreeOfParallelism: 2);

        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref layerZeroStarted) == 2,
            TimeSpan.FromSeconds(5)));
        layerZeroRelease.SetResult();
        var execution = await task;

        Assert.Equal(2, execution.MaxObservedParallelism);
        Assert.Equal(2, execution.MaxDegreeOfParallelism);
        Assert.Equal(2, execution.Layers[0].ObservedParallelism);
        Assert.Equal(["A", "B"], execution.Layers[0].Modules.Select(static item => item.ModuleKey));
        Assert.Equal(["C"], execution.Layers[1].Modules.Select(static item => item.ModuleKey));
        Assert.Equal(2, execution.RestoredModules);
        Assert.Equal(1, execution.CompiledModules);
    }

    [Fact]
    public async Task ArtifactRestoreExecutor_ExecuteAsync_RunsSynchronousCallbacksInParallel()
    {
        var plan = new ProjectModuleArtifactRestorePlan(
            ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
            [
                new ProjectModuleArtifactRestoreLayer(
                    0,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "A",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true),
                        new ProjectModuleArtifactRestoreItem(
                            "B",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true)
                    ],
                    RestoreCount: 2,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 2,
            RestoreModules: 2,
            BlockedModules: 0,
            ReadyArtifactModules: 0,
            MaxRestoreParallelWidth: 2);
        var running = 0;
        var observed = 0;

        var execution = await ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
            plan,
            (_, _) =>
            {
                var current = Interlocked.Increment(ref running);
                UpdateMax(ref observed, current);
                var bothRunning = SpinWait.SpinUntil(
                    () => Volatile.Read(ref running) == 2,
                    TimeSpan.FromSeconds(5));
                Interlocked.Decrement(ref running);
                Assert.True(bothRunning);
                return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
            },
            (_, _) => ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed),
            maxDegreeOfParallelism: 2);

        Assert.Equal(2, execution.MaxObservedParallelism);
        Assert.Equal(2, observed);
        Assert.Equal(2, execution.RestoredModules);
    }

    [Fact]
    public async Task ArtifactRestoreExecutor_ExecuteAsync_StopsBeforeNextLayerWhenTaskFails()
    {
        var plan = new ProjectModuleArtifactRestorePlan(
            ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
            [
                new ProjectModuleArtifactRestoreLayer(
                    0,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "A",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true)
                    ],
                    RestoreCount: 1,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0),
                new ProjectModuleArtifactRestoreLayer(
                    1,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "B",
                            ProjectModuleArtifactRestoreAction.Compile,
                            SemanticReady: false,
                            TypedSemanticReady: false,
                            MirReady: false)
                    ],
                    RestoreCount: 0,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 2,
            RestoreModules: 1,
            BlockedModules: 0,
            ReadyArtifactModules: 0,
            MaxRestoreParallelWidth: 1);
        var compiled = new List<string>();

        var execution = await ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
            plan,
            (_, _) => ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("restore failed")),
            (item, _) =>
            {
                compiled.Add(item.ModuleKey);
                return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
            });

        Assert.True(execution.HasRealTaskExecution);
        Assert.Equal(1, execution.FailedModules);
        Assert.Empty(compiled);
        Assert.Single(execution.Layers);
        var item = Assert.Single(execution.Layers[0].Modules);
        Assert.Equal("A", item.ModuleKey);
        Assert.Equal(ProjectModuleExecutionItemStatus.Failed, item.Status);
        Assert.Equal("restore failed", item.Message);
    }

    private static ProjectModuleArtifactRestorePlan SingleRestorePlan() =>
        new(
            ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
            [
                new ProjectModuleArtifactRestoreLayer(
                    0,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "A",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady: true,
                            TypedSemanticReady: true,
                            MirReady: true)
                    ],
                    RestoreCount: 1,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 1,
            RestoreModules: 1,
            BlockedModules: 0,
            ReadyArtifactModules: 0,
            MaxRestoreParallelWidth: 1);

    private static ProjectModuleDependencySignatureSnapshot DependencySignatures(
        string semanticDependencyHash,
        string typedDependencyHash,
        string memberDependencyHash,
        string mirDependencyHash) =>
        new(
            ProjectModuleDependencySignatureSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleDependencySignatureNode(
                    "A",
                    [],
                    "source-a",
                    "input-a",
                    "",
                    semanticDependencyHash,
                    typedDependencyHash,
                    memberDependencyHash,
                    mirDependencyHash,
                    "",
                    $"{semanticDependencyHash}:{typedDependencyHash}:{memberDependencyHash}:{mirDependencyHash}",
                    SourceAvailable: true,
                    SemanticAvailable: true,
                    TypedAvailable: true,
                    MemberIndexAvailable: true,
                    MirAvailable: true)
            ]);

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
