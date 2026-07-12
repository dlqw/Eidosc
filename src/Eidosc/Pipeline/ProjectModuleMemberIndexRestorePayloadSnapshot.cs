namespace Eidosc.Pipeline;

public sealed record ProjectModuleMemberIndexRestorePayloadSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleMemberIndexRestorePayloadItem> Modules,
    int RestoreModules,
    int LoadedModules,
    int ValidatedModules,
    int StaleModules,
    int MissingModules)
{
    public static ProjectModuleMemberIndexRestorePayloadSnapshot Empty { get; } =
        new("module-member-index-restore-payload-snapshot-v1", [], 0, 0, 0, 0, 0);

    public static ProjectModuleMemberIndexRestorePayloadSnapshot Load(
        ProjectModuleMemberIndexRestorePlan? plan,
        ProjectModuleMemberIndexSnapshot? previous)
    {
        if (plan == null)
        {
            return Empty;
        }

        var previousByModule = previous?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                               new Dictionary<string, ProjectModuleMemberIndexNode>(StringComparer.Ordinal);
        var modules = new List<ProjectModuleMemberIndexRestorePayloadItem>();
        foreach (var item in plan.Modules
                     .Where(static module => module.Action == ProjectModuleMemberIndexRestoreAction.Restore)
                     .OrderBy(static module => module.ModuleKey, StringComparer.Ordinal))
        {
            if (!previousByModule.TryGetValue(item.ModuleKey, out var previousNode))
            {
                modules.Add(ProjectModuleMemberIndexRestorePayloadItem.CreateMissing(item.ModuleKey, item.PreviousMemberIndexHash));
                continue;
            }

            var validated =
                string.Equals(previousNode.LocalIndexHash, item.PreviousLocalIndexHash, StringComparison.Ordinal) &&
                string.Equals(previousNode.DependencyIndexHash, item.PreviousDependencyIndexHash, StringComparison.Ordinal) &&
                string.Equals(previousNode.MemberIndexHash, item.PreviousMemberIndexHash, StringComparison.Ordinal);
            modules.Add(new ProjectModuleMemberIndexRestorePayloadItem(
                item.ModuleKey,
                Loaded: true,
                Validated: validated,
                Missing: false,
                Stale: !validated,
                item.PreviousLocalIndexHash,
                previousNode.LocalIndexHash,
                item.PreviousDependencyIndexHash,
                previousNode.DependencyIndexHash,
                item.PreviousMemberIndexHash,
                previousNode.MemberIndexHash));
        }

        return new ProjectModuleMemberIndexRestorePayloadSnapshot(
            "module-member-index-restore-payload-snapshot-v1",
            modules,
            RestoreModules: modules.Count,
            LoadedModules: modules.Count(static module => module.Loaded),
            ValidatedModules: modules.Count(static module => module.Validated),
            StaleModules: modules.Count(static module => module.Stale),
            MissingModules: modules.Count(static module => module.Missing));
    }
}

public sealed record ProjectModuleMemberIndexRestorePayloadItem(
    string ModuleKey,
    bool Loaded,
    bool Validated,
    bool Missing,
    bool Stale,
    string ExpectedLocalIndexHash,
    string ActualLocalIndexHash,
    string ExpectedDependencyIndexHash,
    string ActualDependencyIndexHash,
    string ExpectedMemberIndexHash,
    string ActualMemberIndexHash)
{
    public static ProjectModuleMemberIndexRestorePayloadItem CreateMissing(
        string moduleKey,
        string expectedMemberIndexHash) =>
        new(
            moduleKey,
            Loaded: false,
            Validated: false,
            Missing: true,
            Stale: false,
            ExpectedLocalIndexHash: "",
            ActualLocalIndexHash: "",
            ExpectedDependencyIndexHash: "",
            ActualDependencyIndexHash: "",
            expectedMemberIndexHash,
            ActualMemberIndexHash: "");
}
