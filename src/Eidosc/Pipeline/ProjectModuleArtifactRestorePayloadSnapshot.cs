namespace Eidosc.Pipeline;

public sealed record ProjectModuleArtifactRestorePayloadSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleArtifactRestorePayloadItem> Modules,
    int RestoreModules,
    int LoadedModules,
    int ValidatedModules,
    int StaleModules,
    int MissingModules,
    int FailedModules)
{
    public static ProjectModuleArtifactRestorePayloadSnapshot Empty { get; } =
        new("module-artifact-restore-payload-v1", [], 0, 0, 0, 0, 0, 0);

    public static ProjectModuleArtifactRestorePayloadSnapshot LoadSemantic(
        ProjectModuleArtifactRestorePlan plan,
        ProjectModuleSemanticSignatureSnapshot semanticSnapshot,
        Func<string, string, string, string, ProjectModuleSemanticSignatureNode?> semanticLoader)
    {
        var semanticByModule = semanticSnapshot.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var modules = new List<ProjectModuleArtifactRestorePayloadItem>();

        foreach (var module in plan.Layers.SelectMany(static layer => layer.Modules)
                     .Where(static item => item.Action == ProjectModuleArtifactRestoreAction.Restore)
                     .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal))
        {
            var semantic = semanticByModule.TryGetValue(module.ModuleKey, out var semanticNode)
                ? semanticLoader(
                    module.ModuleKey,
                    ProjectModuleArtifactKinds.SemanticSignature,
                    semanticNode.ExportSurfaceHash,
                    semanticNode.DependencySemanticSignatureHash)
                : null;

            modules.Add(new ProjectModuleArtifactRestorePayloadItem(
                module.ModuleKey,
                semantic,
                TypedSemantic: null,
                Mir: null,
                SemanticLoaded: semantic != null,
                TypedSemanticLoaded: true,
                MirLoaded: true,
                SemanticHashMatches: semanticNode != null && semantic != null && SemanticMatches(semanticNode, semantic),
                TypedSemanticHashMatches: true,
                MirHashMatches: true));
        }

        return Create(modules);
    }

    public static ProjectModuleArtifactRestorePayloadSnapshot LoadTypedState(
        ProjectModuleArtifactRestorePlan plan,
        ProjectModuleTypedSemanticSnapshot typedSemanticSnapshot,
        Func<string, string, string, string, ProjectModuleTypedSemanticNode?> typedLoader)
    {
        var typedByModule = typedSemanticSnapshot.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var modules = new List<ProjectModuleArtifactRestorePayloadItem>();

        foreach (var module in plan.Layers.SelectMany(static layer => layer.Modules)
                     .Where(static item => item.Action == ProjectModuleArtifactRestoreAction.Restore)
                     .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal))
        {
            var typed = typedByModule.TryGetValue(module.ModuleKey, out var typedNode)
                ? typedLoader(
                    module.ModuleKey,
                    ProjectModuleArtifactKinds.TypesStatePayload,
                    typedNode.LocalSurfaceHash,
                    typedNode.DependencyTypedSemanticHash)
                : null;

            modules.Add(new ProjectModuleArtifactRestorePayloadItem(
                module.ModuleKey,
                Semantic: null,
                typed,
                Mir: null,
                SemanticLoaded: true,
                TypedSemanticLoaded: typed != null,
                MirLoaded: true,
                SemanticHashMatches: true,
                TypedSemanticHashMatches: typedNode != null && typed != null && TypedSemanticMatches(typedNode, typed),
                MirHashMatches: true));
        }

        return Create(modules);
    }

    public static ProjectModuleArtifactRestorePayloadSnapshot LoadTypesStatePayload(
        ProjectModuleArtifactRestorePlan plan,
        Func<string, string, string, string, ProjectModuleTypedSemanticNode?> typedLoader)
    {
        var modules = new List<ProjectModuleArtifactRestorePayloadItem>();

        foreach (var module in plan.Layers.SelectMany(static layer => layer.Modules)
                     .Where(static item => item.Action == ProjectModuleArtifactRestoreAction.Restore)
                     .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal))
        {
            var typed = typedLoader(
                module.ModuleKey,
                ProjectModuleArtifactKinds.TypesStatePayload,
                "",
                "");

            modules.Add(new ProjectModuleArtifactRestorePayloadItem(
                module.ModuleKey,
                Semantic: null,
                typed,
                Mir: null,
                SemanticLoaded: true,
                TypedSemanticLoaded: typed != null,
                MirLoaded: true,
                SemanticHashMatches: true,
                TypedSemanticHashMatches: typed != null,
                MirHashMatches: true));
        }

        return Create(modules);
    }

    public static ProjectModuleArtifactRestorePayloadSnapshot Load(
        ProjectModuleArtifactRestorePlan plan,
        ProjectModuleSemanticSignatureSnapshot semanticSnapshot,
        ProjectModuleTypedSemanticSnapshot typedSemanticSnapshot,
        ProjectModuleMirArtifactSnapshot mirArtifactSnapshot,
        Func<string, string, string, string, ProjectModuleSemanticSignatureNode?> semanticLoader,
        Func<string, string, string, string, ProjectModuleTypedSemanticNode?> typedLoader,
        Func<string, string, string, string, ProjectModuleMirArtifactNode?> mirLoader)
    {
        var semanticByModule = semanticSnapshot.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var typedByModule = typedSemanticSnapshot.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var mirByModule = mirArtifactSnapshot.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var modules = new List<ProjectModuleArtifactRestorePayloadItem>();

        foreach (var module in plan.Layers.SelectMany(static layer => layer.Modules)
                     .Where(static item => item.Action == ProjectModuleArtifactRestoreAction.Restore)
                     .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal))
        {
            var semantic = semanticByModule.TryGetValue(module.ModuleKey, out var semanticNode)
                ? semanticLoader(
                    module.ModuleKey,
                    ProjectModuleArtifactKinds.SemanticSignature,
                    semanticNode.ExportSurfaceHash,
                    semanticNode.DependencySemanticSignatureHash)
                : null;
            var typed = typedByModule.TryGetValue(module.ModuleKey, out var typedNode)
                ? typedLoader(
                    module.ModuleKey,
                    ProjectModuleArtifactKinds.TypedSemanticSignature,
                    typedNode.LocalSurfaceHash,
                    typedNode.DependencyTypedSemanticHash)
                : null;
            var mir = mirByModule.TryGetValue(module.ModuleKey, out var mirNode)
                ? mirLoader(
                    module.ModuleKey,
                    ProjectModuleArtifactKinds.MirArtifact,
                    mirNode.TypedSemanticHash,
                    mirNode.MirArtifactHash)
                : null;

            modules.Add(new ProjectModuleArtifactRestorePayloadItem(
                module.ModuleKey,
                semantic,
                typed,
                mir,
                SemanticLoaded: semantic != null,
                TypedSemanticLoaded: typed != null,
                MirLoaded: mir != null,
                SemanticHashMatches: semanticNode != null && semantic != null && SemanticMatches(semanticNode, semantic),
                TypedSemanticHashMatches: typedNode != null && typed != null && TypedSemanticMatches(typedNode, typed),
                MirHashMatches: mirNode != null && mir != null && MirMatches(mirNode, mir)));
        }

        return Create(modules);
    }

    private static ProjectModuleArtifactRestorePayloadSnapshot Create(
        IReadOnlyList<ProjectModuleArtifactRestorePayloadItem> modules)
    {
        var items = modules.ToArray();
        return new ProjectModuleArtifactRestorePayloadSnapshot(
            "module-artifact-restore-payload-v1",
            items,
            items.Length,
            items.Count(static item => item.Loaded),
            items.Count(static item => item.Validated),
            items.Count(static item => item.Stale),
            items.Count(static item => item.Missing),
            items.Count(static item => item.Failed));
    }

    private static bool SemanticMatches(
        ProjectModuleSemanticSignatureNode expected,
        ProjectModuleSemanticSignatureNode actual) =>
        string.Equals(expected.ModuleKey, actual.ModuleKey, StringComparison.Ordinal) &&
        string.Equals(expected.ExportSurfaceHash, actual.ExportSurfaceHash, StringComparison.Ordinal) &&
        string.Equals(expected.DependencySemanticSignatureHash, actual.DependencySemanticSignatureHash, StringComparison.Ordinal) &&
        string.Equals(expected.SemanticSignatureHash, actual.SemanticSignatureHash, StringComparison.Ordinal);

    private static bool TypedSemanticMatches(
        ProjectModuleTypedSemanticNode expected,
        ProjectModuleTypedSemanticNode actual) =>
        string.Equals(expected.ModuleKey, actual.ModuleKey, StringComparison.Ordinal) &&
        string.Equals(expected.LocalSurfaceHash, actual.LocalSurfaceHash, StringComparison.Ordinal) &&
        string.Equals(expected.DependencyTypedSemanticHash, actual.DependencyTypedSemanticHash, StringComparison.Ordinal) &&
        string.Equals(expected.TypedSemanticHash, actual.TypedSemanticHash, StringComparison.Ordinal);

    private static bool MirMatches(
        ProjectModuleMirArtifactNode expected,
        ProjectModuleMirArtifactNode actual) =>
        string.Equals(expected.ModuleKey, actual.ModuleKey, StringComparison.Ordinal) &&
        string.Equals(expected.TypedSemanticHash, actual.TypedSemanticHash, StringComparison.Ordinal) &&
        string.Equals(expected.MirFunctionModuleFingerprint, actual.MirFunctionModuleFingerprint, StringComparison.Ordinal) &&
        string.Equals(expected.MirArtifactHash, actual.MirArtifactHash, StringComparison.Ordinal);
}

public sealed record ProjectModuleArtifactRestorePayloadItem(
    string ModuleKey,
    ProjectModuleSemanticSignatureNode? Semantic,
    ProjectModuleTypedSemanticNode? TypedSemantic,
    ProjectModuleMirArtifactNode? Mir,
    bool SemanticLoaded,
    bool TypedSemanticLoaded,
    bool MirLoaded,
    bool SemanticHashMatches,
    bool TypedSemanticHashMatches,
    bool MirHashMatches)
{
    public bool Loaded => SemanticLoaded && TypedSemanticLoaded && MirLoaded;
    public bool Validated => Loaded && SemanticHashMatches && TypedSemanticHashMatches && MirHashMatches;
    public bool Stale => Loaded && !Validated;
    public bool Missing => !Loaded;
    public bool Failed => false;
}
