namespace Eidosc.Pipeline;

public sealed record ProjectModuleBuildSchedule(
    IReadOnlyList<ProjectModuleBuildLayer> Layers,
    int MaxParallelWidth)
{
    public static ProjectModuleBuildSchedule FromGraphSnapshot(ProjectModuleGraphSnapshot graph)
    {
        var nodesByKey = graph.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var layers = new List<ProjectModuleBuildLayer>(graph.TopologicalLayers.Count);
        for (var layerIndex = 0; layerIndex < graph.TopologicalLayers.Count; layerIndex++)
        {
            var modules = graph.TopologicalLayers[layerIndex]
                .OrderBy(static module => module, StringComparer.Ordinal)
                .Select(moduleKey => CreateWorkItem(layerIndex, moduleKey, nodesByKey))
                .ToArray();
            layers.Add(new ProjectModuleBuildLayer(layerIndex, modules));
        }

        return new ProjectModuleBuildSchedule(
            layers,
            layers.Count == 0 ? 0 : layers.Max(static layer => layer.Modules.Count));
    }

    private static ProjectModuleBuildItem CreateWorkItem(
        int layerIndex,
        string moduleKey,
        IReadOnlyDictionary<string, ProjectModuleGraphNode> nodesByKey)
    {
        if (!nodesByKey.TryGetValue(moduleKey, out var node))
        {
            return new ProjectModuleBuildItem(layerIndex, moduleKey, [], [], []);
        }

        return new ProjectModuleBuildItem(
            layerIndex,
            moduleKey,
            node.SourcePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
            node.Dependencies.OrderBy(static dependency => dependency, StringComparer.Ordinal).ToArray(),
            node.Dependents.OrderBy(static dependent => dependent, StringComparer.Ordinal).ToArray());
    }
}

public sealed record ProjectModuleBuildLayer(
    int Index,
    IReadOnlyList<ProjectModuleBuildItem> Modules);

public sealed record ProjectModuleBuildItem(
    int LayerIndex,
    string ModuleKey,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Dependents);
