namespace Eidosc.Pipeline;

public sealed record ProjectModuleMemberIndexRestorePlan(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleMemberIndexRestoreItem> Modules,
    int TotalModules,
    int RestoreModules,
    int RebuildModules,
    int AddedModules,
    int RemovedModules)
{
    public const string CurrentSchemaVersion = "module-member-index-restore-plan-v1";

    public ProjectModuleMemberIndexRestorePlan GateWithPayload(
        ProjectModuleMemberIndexRestorePayloadSnapshot payload)
    {
        var validatedModules = payload.Modules
            .Where(static module => module.Validated)
            .Select(static module => module.ModuleKey)
            .ToHashSet(StringComparer.Ordinal);
        var modules = Modules
            .Select(module => module.Action == ProjectModuleMemberIndexRestoreAction.Restore &&
                              !validatedModules.Contains(module.ModuleKey)
                ? module with { Action = ProjectModuleMemberIndexRestoreAction.Rebuild }
                : module)
            .OrderBy(static item => item.Action)
            .ThenBy(static item => item.ModuleKey, StringComparer.Ordinal)
            .ToArray();

        return new ProjectModuleMemberIndexRestorePlan(
            SchemaVersion,
            modules,
            modules.Length,
            modules.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Restore),
            modules.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Rebuild),
            modules.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Add),
            modules.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Remove));
    }

    public static ProjectModuleMemberIndexRestorePlan Create(
        ProjectModuleMemberIndexSnapshot? previous,
        ProjectModuleMemberIndexSnapshot current)
    {
        if (previous == null)
        {
            return new ProjectModuleMemberIndexRestorePlan(
                CurrentSchemaVersion,
                current.Nodes
                    .Select(static node => CreateAdded(node))
                    .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal)
                    .ToArray(),
                current.Nodes.Count,
                RestoreModules: 0,
                RebuildModules: 0,
                AddedModules: current.Nodes.Count,
                RemovedModules: 0);
        }

        var previousByModule = previous.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var currentByModule = current.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var items = new List<ProjectModuleMemberIndexRestoreItem>(current.Nodes.Count + previous.Nodes.Count);

        foreach (var currentNode in current.Nodes.OrderBy(static node => node.ModuleKey, StringComparer.Ordinal))
        {
            if (!previousByModule.TryGetValue(currentNode.ModuleKey, out var previousNode))
            {
                items.Add(CreateAdded(currentNode));
                continue;
            }

            items.Add(new ProjectModuleMemberIndexRestoreItem(
                currentNode.ModuleKey,
                string.Equals(previousNode.MemberIndexHash, currentNode.MemberIndexHash, StringComparison.Ordinal)
                    ? ProjectModuleMemberIndexRestoreAction.Restore
                    : ProjectModuleMemberIndexRestoreAction.Rebuild,
                previousNode.LocalIndexHash,
                currentNode.LocalIndexHash,
                previousNode.DependencyIndexHash,
                currentNode.DependencyIndexHash,
                previousNode.MemberIndexHash,
                currentNode.MemberIndexHash));
        }

        foreach (var previousNode in previous.Nodes.OrderBy(static node => node.ModuleKey, StringComparer.Ordinal))
        {
            if (currentByModule.ContainsKey(previousNode.ModuleKey))
            {
                continue;
            }

            items.Add(new ProjectModuleMemberIndexRestoreItem(
                previousNode.ModuleKey,
                ProjectModuleMemberIndexRestoreAction.Remove,
                previousNode.LocalIndexHash,
                CurrentLocalIndexHash: "",
                previousNode.DependencyIndexHash,
                CurrentDependencyIndexHash: "",
                previousNode.MemberIndexHash,
                CurrentMemberIndexHash: ""));
        }

        var sorted = items
            .OrderBy(static item => item.Action)
            .ThenBy(static item => item.ModuleKey, StringComparer.Ordinal)
            .ToArray();
        return new ProjectModuleMemberIndexRestorePlan(
            CurrentSchemaVersion,
            sorted,
            sorted.Length,
            sorted.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Restore),
            sorted.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Rebuild),
            sorted.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Add),
            sorted.Count(static item => item.Action == ProjectModuleMemberIndexRestoreAction.Remove));
    }

    private static ProjectModuleMemberIndexRestoreItem CreateAdded(ProjectModuleMemberIndexNode currentNode) =>
        new(
            currentNode.ModuleKey,
            ProjectModuleMemberIndexRestoreAction.Add,
            PreviousLocalIndexHash: "",
            currentNode.LocalIndexHash,
            PreviousDependencyIndexHash: "",
            currentNode.DependencyIndexHash,
            PreviousMemberIndexHash: "",
            currentNode.MemberIndexHash);
}

public sealed record ProjectModuleMemberIndexRestoreItem(
    string ModuleKey,
    ProjectModuleMemberIndexRestoreAction Action,
    string PreviousLocalIndexHash,
    string CurrentLocalIndexHash,
    string PreviousDependencyIndexHash,
    string CurrentDependencyIndexHash,
    string PreviousMemberIndexHash,
    string CurrentMemberIndexHash);

public enum ProjectModuleMemberIndexRestoreAction
{
    Restore,
    Rebuild,
    Add,
    Remove
}
