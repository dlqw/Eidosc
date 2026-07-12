namespace Eidosc.CodeGen.Llvm;

public sealed record LlvmObjectGroupRestorePlanSnapshot(
    string SchemaVersion,
    IReadOnlyList<LlvmObjectGroupRestorePlanEntry> Groups)
{
    public static LlvmObjectGroupRestorePlanSnapshot Create(
        IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> groups,
        LlvmFunctionFragmentRestorePlanSnapshot functionPlan)
    {
        var functionActions = functionPlan.Functions.ToDictionary(
            static entry => entry.FunctionKey,
            static entry => entry.Action,
            StringComparer.Ordinal);
        var entries = groups
            .Select(group =>
            {
                var memberActions = group.MemberFunctionKeys
                    .Select(functionKey => functionActions.TryGetValue(functionKey, out var action)
                        ? action
                        : LlvmFunctionFragmentRestoreAction.Rebuild)
                    .ToArray();
                var restoreFunctions = memberActions.Count(static action => action == LlvmFunctionFragmentRestoreAction.Restore);
                var rebuildFunctions = memberActions.Length - restoreFunctions;
                var action = memberActions.Length > 0 &&
                    memberActions.All(static memberAction => memberAction == LlvmFunctionFragmentRestoreAction.Restore)
                        ? LlvmObjectGroupRestoreAction.Restore
                        : LlvmObjectGroupRestoreAction.Rebuild;
                return new LlvmObjectGroupRestorePlanEntry(
                    group.GroupKey,
                    group.RootFunctionKey,
                    action,
                    group.MemberFunctionKeys,
                    restoreFunctions,
                    rebuildFunctions,
                    group.TotalIrBytes);
            })
            .OrderBy(static entry => entry.RootFunctionKey, StringComparer.Ordinal)
            .ToArray();

        return new LlvmObjectGroupRestorePlanSnapshot(
            "llvm-object-group-restore-plan-snapshot-v1",
            entries);
    }

    public int Count(LlvmObjectGroupRestoreAction action) =>
        Groups.Count(group => group.Action == action);
}

public sealed record LlvmObjectGroupRestorePlanEntry(
    string GroupKey,
    string RootFunctionKey,
    LlvmObjectGroupRestoreAction Action,
    IReadOnlyList<string> MemberFunctionKeys,
    int RestoreFunctions,
    int RebuildFunctions,
    int TotalIrBytes);

public enum LlvmObjectGroupRestoreAction
{
    Restore,
    Rebuild
}
