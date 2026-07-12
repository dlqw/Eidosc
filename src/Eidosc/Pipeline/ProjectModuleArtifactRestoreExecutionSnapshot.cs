namespace Eidosc.Pipeline;

public sealed record ProjectModuleArtifactRestoreExecutionSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleArtifactRestoreExecutionLayer> Layers,
    int TotalModules,
    int RestoredModules,
    int BlockedModules,
    int CompiledModules,
    int ReadyArtifactModules,
    int MaxRestoredParallelWidth,
    int MaxCompiledParallelWidth,
    int FailedModules = 0,
    int SkippedModules = 0,
    bool HasRealTaskExecution = false,
    double ElapsedMs = 0,
    int MaxObservedParallelism = 0,
    int MaxDegreeOfParallelism = 0)
{
    public const string CurrentSchemaVersion = "module-artifact-restore-execution-v2";

    public static ProjectModuleArtifactRestoreExecutionSnapshot FromRestorePlan(
        ProjectModuleArtifactRestorePlan plan)
    {
        var layers = plan.Layers
            .Select(layer =>
            {
                var modules = layer.Modules
                    .Select(static item => new ProjectModuleArtifactRestoreExecutionItem(
                        item.ModuleKey,
                        ToExecutionAction(item.Action),
                        item.SemanticReady,
                        item.TypedSemanticReady,
                        item.MirReady))
                    .ToArray();

                return new ProjectModuleArtifactRestoreExecutionLayer(
                    layer.Index,
                    modules,
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.Restored),
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.Blocked),
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.Compiled),
                    modules.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.ReadyArtifact));
            })
            .ToArray();

        return new ProjectModuleArtifactRestoreExecutionSnapshot(
            CurrentSchemaVersion,
            layers,
            layers.Sum(static layer => layer.Modules.Count),
            layers.Sum(static layer => layer.RestoredCount),
            layers.Sum(static layer => layer.BlockedCount),
            layers.Sum(static layer => layer.CompiledCount),
            layers.Sum(static layer => layer.ReadyArtifactCount),
            layers.Length == 0 ? 0 : layers.Max(static layer => layer.RestoredCount),
            layers.Length == 0 ? 0 : layers.Max(static layer => layer.CompiledCount));
    }

    private static ProjectModuleArtifactRestoreExecutionAction ToExecutionAction(
        ProjectModuleArtifactRestoreAction action)
    {
        return action switch
        {
            ProjectModuleArtifactRestoreAction.Restore => ProjectModuleArtifactRestoreExecutionAction.Restored,
            ProjectModuleArtifactRestoreAction.Blocked => ProjectModuleArtifactRestoreExecutionAction.Blocked,
            ProjectModuleArtifactRestoreAction.ReadyArtifact => ProjectModuleArtifactRestoreExecutionAction.ReadyArtifact,
            _ => ProjectModuleArtifactRestoreExecutionAction.Compiled
        };
    }
}

public sealed record ProjectModuleArtifactRestoreExecutionLayer(
    int Index,
    IReadOnlyList<ProjectModuleArtifactRestoreExecutionItem> Modules,
    int RestoredCount,
    int BlockedCount,
    int CompiledCount,
    int ReadyArtifactCount,
    int FailedCount = 0,
    int SkippedCount = 0,
    double ElapsedMs = 0,
    int ObservedParallelism = 0);

public sealed record ProjectModuleArtifactRestoreExecutionItem(
    string ModuleKey,
    ProjectModuleArtifactRestoreExecutionAction Action,
    bool SemanticReady,
    bool TypedSemanticReady,
    bool MirReady,
    ProjectModuleExecutionItemStatus Status = ProjectModuleExecutionItemStatus.Completed,
    string? Message = null);

public enum ProjectModuleArtifactRestoreExecutionAction
{
    Restored,
    Blocked,
    Compiled,
    ReadyArtifact
}
