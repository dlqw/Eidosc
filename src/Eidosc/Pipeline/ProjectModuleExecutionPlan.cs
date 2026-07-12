namespace Eidosc.Pipeline;

public sealed record ProjectModuleExecutionPlan(
    IReadOnlyList<ProjectModuleExecutionLayer> Layers,
    int TotalModules,
    int CompileModules,
    int RestoreModules,
    int ReadyArtifactModules,
    int MaxCompileParallelWidth,
    int MaxRestoreParallelWidth,
    int MaxReadyArtifactParallelWidth)
{
    public static ProjectModuleExecutionPlan FromSchedule(
        ProjectModuleBuildSchedule schedule,
        ProjectModuleInvalidationPlan invalidation,
        Func<ProjectModuleBuildItem, bool>? readyArtifactPredicate = null)
    {
        var affected = invalidation.AffectedModules.ToHashSet(StringComparer.Ordinal);
        var layers = schedule.Layers
            .Select(layer =>
            {
                var items = layer.Modules
                    .Select(module => new ProjectModuleExecutionItem(
                        module.ModuleKey,
                        SelectAction(module, affected, readyArtifactPredicate),
                        module.SourcePaths,
                        module.Dependencies,
                        module.Dependents))
                    .OrderBy(static item => item.Action)
                    .ThenBy(static item => item.ModuleKey, StringComparer.Ordinal)
                    .ToArray();

                return new ProjectModuleExecutionLayer(
                    layer.Index,
                    items,
                    items.Count(static item => item.Action == ProjectModuleExecutionAction.Compile),
                    items.Count(static item => item.Action == ProjectModuleExecutionAction.Restore),
                    items.Count(static item => item.Action == ProjectModuleExecutionAction.ReadyArtifact));
            })
            .ToArray();

        return new ProjectModuleExecutionPlan(
            layers,
            layers.Sum(static layer => layer.Modules.Count),
            layers.Sum(static layer => layer.CompileCount),
            layers.Sum(static layer => layer.RestoreCount),
            layers.Sum(static layer => layer.ReadyArtifactCount),
            layers.Length == 0 ? 0 : layers.Max(static layer => layer.CompileCount),
            layers.Length == 0 ? 0 : layers.Max(static layer => layer.RestoreCount),
            layers.Length == 0 ? 0 : layers.Max(static layer => layer.ReadyArtifactCount));
    }

    public static bool IsPrecompiledReadyArtifact(ProjectModuleBuildItem module)
    {
        return module.SourcePaths.Count > 0 &&
               module.SourcePaths.All(IsPrecompiledSourcePath);
    }

    private static ProjectModuleExecutionAction SelectAction(
        ProjectModuleBuildItem module,
        HashSet<string> affected,
        Func<ProjectModuleBuildItem, bool>? readyArtifactPredicate)
    {
        if (readyArtifactPredicate?.Invoke(module) == true)
        {
            return ProjectModuleExecutionAction.ReadyArtifact;
        }

        return affected.Contains(module.ModuleKey)
            ? ProjectModuleExecutionAction.Compile
            : ProjectModuleExecutionAction.Restore;
    }

    private static bool IsPrecompiledSourcePath(string sourcePath)
    {
        if (sourcePath.StartsWith("<precompiled:", StringComparison.Ordinal) &&
            sourcePath.EndsWith('>'))
        {
            return true;
        }

        return sourcePath.Replace('\\', '/').Contains("/Stdlib/Precompiled/", StringComparison.Ordinal);
    }
}

public sealed record ProjectModuleExecutionLayer(
    int Index,
    IReadOnlyList<ProjectModuleExecutionItem> Modules,
    int CompileCount,
    int RestoreCount,
    int ReadyArtifactCount);

public sealed record ProjectModuleExecutionItem(
    string ModuleKey,
    ProjectModuleExecutionAction Action,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Dependents);

public enum ProjectModuleExecutionAction
{
    Compile,
    Restore,
    ReadyArtifact
}
