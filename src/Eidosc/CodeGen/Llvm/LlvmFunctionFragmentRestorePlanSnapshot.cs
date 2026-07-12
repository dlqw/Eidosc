namespace Eidosc.CodeGen.Llvm;

public sealed record LlvmFunctionFragmentRestorePlanSnapshot(
    string SchemaVersion,
    IReadOnlyList<LlvmFunctionFragmentRestorePlanEntry> Functions)
{
    public static LlvmFunctionFragmentRestorePlanSnapshot Create(
        LlvmFunctionFragmentSnapshot previous,
        LlvmFunctionFragmentSnapshot current)
    {
        var previousByKey = previous.Functions.ToDictionary(static fragment => fragment.FunctionKey, StringComparer.Ordinal);
        var currentByKey = current.Functions.ToDictionary(static fragment => fragment.FunctionKey, StringComparer.Ordinal);
        var entries = new List<LlvmFunctionFragmentRestorePlanEntry>(current.Functions.Count);

        foreach (var fragment in current.Functions.OrderBy(static fragment => fragment.FunctionKey, StringComparer.Ordinal))
        {
            if (!previousByKey.TryGetValue(fragment.FunctionKey, out var previousFragment))
            {
                entries.Add(CreateEntry(fragment, LlvmFunctionFragmentRestoreAction.Rebuild, previousBodyHash: ""));
                continue;
            }

            entries.Add(CreateEntry(
                fragment,
                string.Equals(previousFragment.BodyHash, fragment.BodyHash, StringComparison.Ordinal)
                    ? LlvmFunctionFragmentRestoreAction.Restore
                    : LlvmFunctionFragmentRestoreAction.Rebuild,
                previousFragment.BodyHash));
        }

        foreach (var removed in previous.Functions
                     .Where(fragment => !currentByKey.ContainsKey(fragment.FunctionKey))
                     .OrderBy(static fragment => fragment.FunctionKey, StringComparer.Ordinal))
        {
            entries.Add(new LlvmFunctionFragmentRestorePlanEntry(
                removed.FunctionKey,
                LlvmFunctionFragmentRestoreAction.Remove,
                removed.BodyHash,
                CurrentBodyHash: "",
                removed.IrFragment.Length,
                removed.BasicBlockCount,
                removed.InstructionCount,
                removed.ParameterCount));
        }

        return new LlvmFunctionFragmentRestorePlanSnapshot(
            "llvm-function-fragment-restore-plan-snapshot-v1",
            entries);
    }

    public int Count(LlvmFunctionFragmentRestoreAction action) =>
        Functions.Count(entry => entry.Action == action);

    private static LlvmFunctionFragmentRestorePlanEntry CreateEntry(
        LlvmFunctionFragment fragment,
        LlvmFunctionFragmentRestoreAction action,
        string previousBodyHash)
    {
        return new LlvmFunctionFragmentRestorePlanEntry(
            fragment.FunctionKey,
            action,
            previousBodyHash,
            fragment.BodyHash,
            fragment.IrFragment.Length,
            fragment.BasicBlockCount,
            fragment.InstructionCount,
            fragment.ParameterCount);
    }
}

public sealed record LlvmFunctionFragmentRestorePlanEntry(
    string FunctionKey,
    LlvmFunctionFragmentRestoreAction Action,
    string PreviousBodyHash,
    string CurrentBodyHash,
    int IrBytes,
    int BasicBlockCount,
    int InstructionCount,
    int ParameterCount);

public enum LlvmFunctionFragmentRestoreAction
{
    Restore,
    Rebuild,
    Remove
}
