namespace Eidosc.Pipeline;

public sealed record ProjectModuleArtifactRestorePlan(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleArtifactRestoreLayer> Layers,
    int TotalModules,
    int RestoreModules,
    int BlockedModules,
    int ReadyArtifactModules,
    int MaxRestoreParallelWidth)
{
    public const string CurrentSchemaVersion = "module-artifact-restore-plan-v2";

    public int CompileModules => Layers.Sum(static layer => layer.CompileCount);

    public int MaxCompileParallelWidth => Layers.Count == 0
        ? 0
        : Layers.Max(static layer => layer.CompileCount);

    public ProjectModuleArtifactRestorePlan GateWithPayload(
        ProjectModuleArtifactRestorePayloadSnapshot payload)
    {
        var validatedModules = payload.Modules
            .Where(static module => module.Validated)
            .Select(static module => module.ModuleKey)
            .ToHashSet(StringComparer.Ordinal);
        var layers = Layers
            .Select(layer =>
            {
                var modules = layer.Modules
                    .Select(module => module.Action == ProjectModuleArtifactRestoreAction.Restore &&
                                      !validatedModules.Contains(module.ModuleKey)
                        ? module with { Action = ProjectModuleArtifactRestoreAction.Compile }
                        : module)
                    .ToArray();
                return new ProjectModuleArtifactRestoreLayer(
                    layer.Index,
                    modules,
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.Restore),
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.Blocked),
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.ReadyArtifact));
            })
            .ToArray();

        return FromLayers(layers);
    }

    public ProjectModuleArtifactRestorePlan GateWithDependencySignatures(
        ProjectModuleDependencySignatureSnapshot? current,
        ProjectModuleDependencySignatureSnapshot? previous,
        ProjectModuleDependencySignatureRequirement requirement)
    {
        if (current == null || previous == null)
        {
            return this;
        }

        var layers = Layers
            .Select(layer =>
            {
                var modules = layer.Modules
                    .Select(module => module.Action == ProjectModuleArtifactRestoreAction.Restore &&
                                      !current.IsCompatibleWith(previous, module.ModuleKey, requirement)
                        ? module with { Action = ProjectModuleArtifactRestoreAction.Compile }
                        : module)
                    .ToArray();
                return new ProjectModuleArtifactRestoreLayer(
                    layer.Index,
                    modules,
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.Restore),
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.Blocked),
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.ReadyArtifact));
            })
            .ToArray();

        return FromLayers(layers);
    }

    public static ProjectModuleArtifactRestorePlan FromExecutionAndReadiness(
        ProjectModuleExecutionPlan executionPlan,
        ProjectModuleArtifactReadinessPlan readiness,
        ProjectModuleArtifactRequirement requirement = ProjectModuleArtifactRequirement.SemanticTypedAndMir)
    {
        var readinessByModule = readiness.Modules.ToDictionary(static item => item.ModuleKey, StringComparer.Ordinal);
        var layers = executionPlan.Layers
            .Select(layer =>
            {
                var items = layer.Modules
                    .Select(module => CreateItem(module, readinessByModule, requirement))
                    .OrderBy(static item => item.Action)
                    .ThenBy(static item => item.ModuleKey, StringComparer.Ordinal)
                    .ToArray();
                return new ProjectModuleArtifactRestoreLayer(
                    layer.Index,
                    items,
                    items.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.Restore),
                    items.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.Blocked),
                    items.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.ReadyArtifact));
            })
            .ToArray();

        return new ProjectModuleArtifactRestorePlan(
            CurrentSchemaVersion,
            layers,
            layers.Sum(static layer => layer.Modules.Count),
            layers.Sum(static layer => layer.RestoreCount),
            layers.Sum(static layer => layer.BlockedCount),
            layers.Sum(static layer => layer.ReadyArtifactCount),
            layers.Length == 0 ? 0 : layers.Max(static layer => layer.RestoreCount));
    }

    private static ProjectModuleArtifactRestorePlan FromLayers(
        IReadOnlyList<ProjectModuleArtifactRestoreLayer> layers) =>
        new(
            CurrentSchemaVersion,
            layers,
            layers.Sum(static layer => layer.Modules.Count),
            layers.Sum(static layer => layer.RestoreCount),
            layers.Sum(static layer => layer.BlockedCount),
            layers.Sum(static layer => layer.ReadyArtifactCount),
            layers.Count == 0 ? 0 : layers.Max(static layer => layer.RestoreCount));

    private static ProjectModuleArtifactRestoreItem CreateItem(
        ProjectModuleExecutionItem module,
        IReadOnlyDictionary<string, ProjectModuleArtifactReadinessItem> readinessByModule,
        ProjectModuleArtifactRequirement requirement)
    {
        if (!readinessByModule.TryGetValue(module.ModuleKey, out var readiness))
        {
            return new ProjectModuleArtifactRestoreItem(
                module.ModuleKey,
                ProjectModuleArtifactRestoreAction.Blocked,
                SemanticReady: false,
                TypedSemanticReady: false,
                MirReady: false);
        }

        return CreateItem(readiness, requirement);
    }

    private static ProjectModuleArtifactRestoreItem CreateItem(
        ProjectModuleArtifactReadinessItem readiness,
        ProjectModuleArtifactRequirement requirement)
    {
        var action = readiness.Action switch
        {
            ProjectModuleExecutionAction.Compile => ProjectModuleArtifactRestoreAction.Compile,
            ProjectModuleExecutionAction.ReadyArtifact => ProjectModuleArtifactRestoreAction.ReadyArtifact,
            ProjectModuleExecutionAction.Restore when IsReady(readiness, requirement) =>
                ProjectModuleArtifactRestoreAction.Restore,
            _ => ProjectModuleArtifactRestoreAction.Blocked
        };

        return new ProjectModuleArtifactRestoreItem(
            readiness.ModuleKey,
            action,
            readiness.SemanticReady,
            readiness.TypedSemanticReady,
            readiness.MirReady);
    }

    private static bool IsReady(
        ProjectModuleArtifactReadinessItem readiness,
        ProjectModuleArtifactRequirement requirement) =>
        requirement switch
        {
            ProjectModuleArtifactRequirement.SemanticOnly => readiness.SemanticReady,
            ProjectModuleArtifactRequirement.SemanticTyped => readiness.SemanticReady && readiness.TypedSemanticReady,
            ProjectModuleArtifactRequirement.SemanticTypedAndMir => readiness.SemanticReady && readiness.TypedSemanticReady && readiness.MirReady,
            _ => false
        };
}

public sealed record ProjectModuleArtifactRestoreLayer(
    int Index,
    IReadOnlyList<ProjectModuleArtifactRestoreItem> Modules,
    int RestoreCount,
    int BlockedCount,
    int ReadyArtifactCount)
{
    public int CompileCount => Modules.Count(static item => item.Action == ProjectModuleArtifactRestoreAction.Compile);
}

public sealed record ProjectModuleArtifactRestoreItem(
    string ModuleKey,
    ProjectModuleArtifactRestoreAction Action,
    bool SemanticReady,
    bool TypedSemanticReady,
    bool MirReady);

public enum ProjectModuleArtifactRestoreAction
{
    Compile,
    Restore,
    Blocked,
    ReadyArtifact
}
