namespace Eidosc.Pipeline;

public sealed record ProjectModuleArtifactReadinessPlan(
    IReadOnlyList<ProjectModuleArtifactReadinessItem> Modules,
    int TotalModules,
    int CompileModules,
    int RestoreModules,
    int ReadyArtifactModules,
    int SemanticReadyModules,
    int SemanticMissingModules,
    int TypedSemanticReadyModules,
    int TypedSemanticMissingModules,
    int MirReadyModules,
    int MirMissingModules)
{
    public static ProjectModuleArtifactReadinessPlan FromExecutionPlan(
        ProjectModuleExecutionPlan executionPlan,
        ProjectModuleSemanticSignatureSnapshot? semanticSnapshot,
        ProjectModuleTypedSemanticSnapshot? typedSemanticSnapshot,
        ProjectModuleMirArtifactSnapshot? mirArtifactSnapshot,
        Func<string, string, string, string, bool> artifactAvailable,
        ProjectModuleArtifactRequirement requirement = ProjectModuleArtifactRequirement.SemanticTypedAndMir)
    {
        var semanticNodes = semanticSnapshot?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                            new Dictionary<string, ProjectModuleSemanticSignatureNode>(StringComparer.Ordinal);
        var typedSemanticNodes = typedSemanticSnapshot?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                                 new Dictionary<string, ProjectModuleTypedSemanticNode>(StringComparer.Ordinal);
        var mirNodes = mirArtifactSnapshot?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                       new Dictionary<string, ProjectModuleMirArtifactNode>(StringComparer.Ordinal);
        var items = executionPlan.Layers
            .SelectMany(static layer => layer.Modules)
            .Select(module =>
            {
                var semanticReady = IsSemanticArtifactReady(module, semanticNodes, artifactAvailable);
                var typedSemanticReady = !RequiresTypedSemantic(requirement) ||
                                         IsTypedSemanticArtifactReady(module, typedSemanticNodes, artifactAvailable);
                var mirReady = !RequiresMir(requirement) ||
                               IsMirArtifactReady(module, mirNodes, artifactAvailable);

                return new ProjectModuleArtifactReadinessItem(
                    module.ModuleKey,
                    module.Action,
                    semanticReady,
                    typedSemanticReady,
                    mirReady);
            })
            .OrderBy(static item => item.Action)
            .ThenBy(static item => item.ModuleKey, StringComparer.Ordinal)
            .ToArray();

        return new ProjectModuleArtifactReadinessPlan(
            items,
            items.Length,
            items.Count(static item => item.Action == ProjectModuleExecutionAction.Compile),
            items.Count(static item => item.Action == ProjectModuleExecutionAction.Restore),
            items.Count(static item => item.Action == ProjectModuleExecutionAction.ReadyArtifact),
            items.Count(static item => item.SemanticReady),
            items.Count(static item => item.Action == ProjectModuleExecutionAction.Restore && !item.SemanticReady),
            items.Count(static item => item.TypedSemanticReady),
            RequiresTypedSemantic(requirement)
                ? items.Count(static item => item.Action == ProjectModuleExecutionAction.Restore && !item.TypedSemanticReady)
                : 0,
            items.Count(static item => item.MirReady),
            RequiresMir(requirement)
                ? items.Count(static item => item.Action == ProjectModuleExecutionAction.Restore && !item.MirReady)
                : 0);
    }

    private static bool RequiresTypedSemantic(ProjectModuleArtifactRequirement requirement) =>
        requirement is ProjectModuleArtifactRequirement.SemanticTyped or
                       ProjectModuleArtifactRequirement.SemanticTypedAndMir;

    private static bool RequiresMir(ProjectModuleArtifactRequirement requirement) =>
        requirement == ProjectModuleArtifactRequirement.SemanticTypedAndMir;

    private static bool ShouldRequireArtifact(ProjectModuleExecutionItem module) =>
        module.Action == ProjectModuleExecutionAction.Restore;

    private static bool IsSemanticArtifactReady(
        ProjectModuleExecutionItem module,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticNodes,
        Func<string, string, string, string, bool> artifactAvailable)
    {
        if (!ShouldRequireArtifact(module))
        {
            return module.Action == ProjectModuleExecutionAction.ReadyArtifact;
        }

        return semanticNodes.TryGetValue(module.ModuleKey, out var node) &&
               artifactAvailable(
                   module.ModuleKey,
                   ProjectModuleArtifactKinds.SemanticSignature,
                   node.ExportSurfaceHash,
                   node.DependencySemanticSignatureHash);
    }

    private static bool IsTypedSemanticArtifactReady(
        ProjectModuleExecutionItem module,
        IReadOnlyDictionary<string, ProjectModuleTypedSemanticNode> typedSemanticNodes,
        Func<string, string, string, string, bool> artifactAvailable)
    {
        if (!ShouldRequireArtifact(module))
        {
            return module.Action == ProjectModuleExecutionAction.ReadyArtifact;
        }

        return typedSemanticNodes.TryGetValue(module.ModuleKey, out var node) &&
               artifactAvailable(
                   module.ModuleKey,
                   ProjectModuleArtifactKinds.TypedSemanticSignature,
                   node.LocalSurfaceHash,
                   node.DependencyTypedSemanticHash);
    }

    private static bool IsMirArtifactReady(
        ProjectModuleExecutionItem module,
        IReadOnlyDictionary<string, ProjectModuleMirArtifactNode> mirNodes,
        Func<string, string, string, string, bool> artifactAvailable)
    {
        if (!ShouldRequireArtifact(module))
        {
            return module.Action == ProjectModuleExecutionAction.ReadyArtifact;
        }

        return mirNodes.TryGetValue(module.ModuleKey, out var node) &&
               artifactAvailable(
                   module.ModuleKey,
                   ProjectModuleArtifactKinds.MirArtifact,
                   node.TypedSemanticHash,
                   node.MirArtifactHash);
    }
}

public sealed record ProjectModuleArtifactReadinessItem(
    string ModuleKey,
    ProjectModuleExecutionAction Action,
    bool SemanticReady,
    bool TypedSemanticReady,
    bool MirReady);

public enum ProjectModuleArtifactRequirement
{
    SemanticOnly,
    SemanticTyped,
    SemanticTypedAndMir
}

public static class ProjectModuleArtifactKinds
{
    public const string SemanticSignature = "module-semantic-signature";
    public const string NamerStatePayload = "module-namer-state-payload";
    public const string TypesStatePayload = "module-types-state-payload";
    public const string HirStatePayload = "module-hir-state-payload";
    public const string MirStatePayload = "module-mir-state-payload";
    public const string TypedSemanticSignature = "module-typed-semantic-signature";
    public const string MirArtifact = "module-mir-artifact";
}
