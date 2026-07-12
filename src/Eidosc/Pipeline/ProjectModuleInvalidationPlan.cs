namespace Eidosc.Pipeline;

public sealed record ProjectModuleInvalidationPlan(
    IReadOnlyList<ProjectModuleInvalidationChange> Changes,
    IReadOnlyList<string> AffectedModules,
    IReadOnlyList<string> UnchangedModules)
{
    public static ProjectModuleInvalidationPlan FromSemanticSignatures(
        ProjectModuleSemanticSignatureSnapshot? previous,
        ProjectModuleSemanticSignatureSnapshot current)
    {
        if (previous == null)
        {
            return new ProjectModuleInvalidationPlan(
                current.Nodes
                    .Select(static node => new ProjectModuleInvalidationChange(
                        node.ModuleKey,
                        ProjectModuleInvalidationReason.Added))
                    .OrderBy(static change => change.ModuleKey, StringComparer.Ordinal)
                    .ToArray(),
                current.Nodes
                    .Select(static node => node.ModuleKey)
                    .OrderBy(static module => module, StringComparer.Ordinal)
                    .ToArray(),
                []);
        }

        var previousByKey = previous.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var currentByKey = current.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var changes = new List<ProjectModuleInvalidationChange>();

        foreach (var (moduleKey, currentNode) in currentByKey.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!previousByKey.TryGetValue(moduleKey, out var previousNode))
            {
                changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.Added));
                continue;
            }

            if (!string.Equals(previousNode.ExportSurfaceHash, currentNode.ExportSurfaceHash, StringComparison.Ordinal))
            {
                changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.ExportSurfaceChanged));
                continue;
            }

            if (!string.Equals(
                    previousNode.DependencySemanticSignatureHash,
                    currentNode.DependencySemanticSignatureHash,
                    StringComparison.Ordinal))
            {
                changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.DependencySignatureChanged));
            }
        }

        foreach (var moduleKey in previousByKey.Keys.Except(currentByKey.Keys, StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal))
        {
            changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.Removed));
        }

        var affected = ComputeAffectedModules(changes, previous.Nodes, current.Nodes);
        var unchanged = currentByKey.Keys
            .Except(affected, StringComparer.Ordinal)
            .OrderBy(static module => module, StringComparer.Ordinal)
            .ToArray();

        return new ProjectModuleInvalidationPlan(
            changes
                .OrderBy(static change => change.ModuleKey, StringComparer.Ordinal)
                .ThenBy(static change => change.Reason)
                .ToArray(),
            affected
                .OrderBy(static module => module, StringComparer.Ordinal)
                .ToArray(),
            unchanged);
    }

    public static ProjectModuleInvalidationPlan FromTypedSemanticSignatures(
        ProjectModuleTypedSemanticSnapshot? previous,
        ProjectModuleTypedSemanticSnapshot current)
    {
        if (previous == null)
        {
            return new ProjectModuleInvalidationPlan(
                current.Nodes
                    .Select(static node => new ProjectModuleInvalidationChange(
                        node.ModuleKey,
                        ProjectModuleInvalidationReason.Added))
                    .OrderBy(static change => change.ModuleKey, StringComparer.Ordinal)
                    .ToArray(),
                current.Nodes
                    .Select(static node => node.ModuleKey)
                    .OrderBy(static module => module, StringComparer.Ordinal)
                    .ToArray(),
                []);
        }

        var previousByKey = previous.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var currentByKey = current.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var changes = new List<ProjectModuleInvalidationChange>();

        foreach (var (moduleKey, currentNode) in currentByKey.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!previousByKey.TryGetValue(moduleKey, out var previousNode))
            {
                changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.Added));
                continue;
            }

            if (!string.Equals(previousNode.LocalSurfaceHash, currentNode.LocalSurfaceHash, StringComparison.Ordinal))
            {
                changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.TypedSurfaceChanged));
                continue;
            }

            if (!string.Equals(
                    previousNode.DependencyTypedSemanticHash,
                    currentNode.DependencyTypedSemanticHash,
                    StringComparison.Ordinal))
            {
                changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.DependencyTypedSignatureChanged));
            }
        }

        foreach (var moduleKey in previousByKey.Keys.Except(currentByKey.Keys, StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal))
        {
            changes.Add(new ProjectModuleInvalidationChange(moduleKey, ProjectModuleInvalidationReason.Removed));
        }

        var affected = ComputeAffectedModules(changes, previous.Nodes, current.Nodes);
        var unchanged = currentByKey.Keys
            .Except(affected, StringComparer.Ordinal)
            .OrderBy(static module => module, StringComparer.Ordinal)
            .ToArray();

        return new ProjectModuleInvalidationPlan(
            changes
                .OrderBy(static change => change.ModuleKey, StringComparer.Ordinal)
                .ThenBy(static change => change.Reason)
                .ToArray(),
            affected
                .OrderBy(static module => module, StringComparer.Ordinal)
                .ToArray(),
            unchanged);
    }

    private static HashSet<string> ComputeAffectedModules(
        IReadOnlyList<ProjectModuleInvalidationChange> changes,
        IReadOnlyList<ProjectModuleSemanticSignatureNode> previousNodes,
        IReadOnlyList<ProjectModuleSemanticSignatureNode> currentNodes)
    {
        return ComputeAffectedModulesCore(
            changes,
            previousNodes.Select(static node => (node.ModuleKey, node.Dependencies)).ToArray(),
            currentNodes.Select(static node => (node.ModuleKey, node.Dependencies)).ToArray());
    }

    private static HashSet<string> ComputeAffectedModules(
        IReadOnlyList<ProjectModuleInvalidationChange> changes,
        IReadOnlyList<ProjectModuleTypedSemanticNode> previousNodes,
        IReadOnlyList<ProjectModuleTypedSemanticNode> currentNodes)
    {
        return ComputeAffectedModulesCore(
            changes,
            previousNodes.Select(static node => (node.ModuleKey, node.Dependencies)).ToArray(),
            currentNodes.Select(static node => (node.ModuleKey, node.Dependencies)).ToArray());
    }

    private static HashSet<string> ComputeAffectedModulesCore(
        IReadOnlyList<ProjectModuleInvalidationChange> changes,
        IReadOnlyList<(string ModuleKey, IReadOnlyList<string> Dependencies)> previousNodes,
        IReadOnlyList<(string ModuleKey, IReadOnlyList<string> Dependencies)> currentNodes)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        var currentDependents = BuildDependents(currentNodes);
        var previousDependents = BuildDependents(previousNodes);

        foreach (var change in changes)
        {
            if (change.Reason != ProjectModuleInvalidationReason.Removed)
            {
                affected.Add(change.ModuleKey);
            }

            queue.Enqueue(change.ModuleKey);
        }

        while (queue.Count > 0)
        {
            var moduleKey = queue.Dequeue();
            EnqueueDependents(moduleKey, currentDependents, affected, queue);
            EnqueueDependents(moduleKey, previousDependents, affected, queue);
        }

        return affected;
    }

    private static Dictionary<string, List<string>> BuildDependents(IReadOnlyList<(string ModuleKey, IReadOnlyList<string> Dependencies)> nodes)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            foreach (var dependency in node.Dependencies)
            {
                if (!dependents.TryGetValue(dependency, out var list))
                {
                    list = [];
                    dependents[dependency] = list;
                }

                list.Add(node.ModuleKey);
            }
        }

        return dependents;
    }

    private static void EnqueueDependents(
        string moduleKey,
        IReadOnlyDictionary<string, List<string>> dependents,
        HashSet<string> affected,
        Queue<string> queue)
    {
        if (!dependents.TryGetValue(moduleKey, out var directDependents))
        {
            return;
        }

        foreach (var dependent in directDependents)
        {
            if (affected.Add(dependent))
            {
                queue.Enqueue(dependent);
            }
        }
    }
}

public sealed record ProjectModuleInvalidationChange(
    string ModuleKey,
    ProjectModuleInvalidationReason Reason);

public enum ProjectModuleInvalidationReason
{
    Added,
    Removed,
    ExportSurfaceChanged,
    DependencySignatureChanged,
    SourceCompilationUnitChanged,
    TypedSurfaceChanged,
    DependencyTypedSignatureChanged
}
