namespace Eidosc.CodeGen.Llvm;

public sealed record LlvmFunctionFragmentRestoreExecution(
    LlvmFunctionFragmentSnapshot Fragments,
    LlvmFunctionFragmentRestoreResultSnapshot Result);

public sealed record LlvmFunctionFragmentRestoreResultSnapshot(
    string SchemaVersion,
    int RestoredFragments,
    int RebuiltFragments,
    int RemovedFragments,
    int FallbackRebuildFragments,
    int RestoredIrBytes,
    int RebuiltIrBytes,
    string OutputModuleFingerprint,
    bool MatchesCurrentIr,
    bool Applied);

public static class LlvmFunctionFragmentRestoreExecutor
{
    public static LlvmFunctionFragmentRestoreExecution Execute(
        LlvmFunctionFragmentSnapshot previous,
        LlvmFunctionFragmentSnapshot current,
        LlvmFunctionFragmentRestorePlanSnapshot plan)
    {
        var previousByKey = previous.Functions.ToDictionary(static fragment => fragment.FunctionKey, StringComparer.Ordinal);
        var planByKey = plan.Functions.ToDictionary(static entry => entry.FunctionKey, StringComparer.Ordinal);
        var fragments = new List<LlvmFunctionFragment>(current.Functions.Count);
        var restoredFragments = 0;
        var rebuiltFragments = 0;
        var fallbackRebuildFragments = 0;
        var restoredIrBytes = 0;
        var rebuiltIrBytes = 0;

        foreach (var currentFragment in current.Functions.OrderBy(static fragment => fragment.FunctionKey, StringComparer.Ordinal))
        {
            if (planByKey.TryGetValue(currentFragment.FunctionKey, out var entry) &&
                entry.Action == LlvmFunctionFragmentRestoreAction.Restore &&
                previousByKey.TryGetValue(currentFragment.FunctionKey, out var previousFragment) &&
                string.Equals(previousFragment.BodyHash, currentFragment.BodyHash, StringComparison.Ordinal))
            {
                fragments.Add(previousFragment);
                restoredFragments++;
                restoredIrBytes += previousFragment.IrFragment.Length;
                continue;
            }

            fragments.Add(currentFragment);
            rebuiltFragments++;
            rebuiltIrBytes += currentFragment.IrFragment.Length;
            if (entry?.Action == LlvmFunctionFragmentRestoreAction.Restore)
            {
                fallbackRebuildFragments++;
            }
        }

        var restoredSnapshot = new LlvmFunctionFragmentSnapshot(
            current.SchemaVersion,
            fragments);
        var removedFragments = plan.Functions.Count(static entry =>
            entry.Action == LlvmFunctionFragmentRestoreAction.Remove);

        return new LlvmFunctionFragmentRestoreExecution(
            restoredSnapshot,
            new LlvmFunctionFragmentRestoreResultSnapshot(
                "llvm-function-fragment-restore-result-snapshot-v1",
                restoredFragments,
                rebuiltFragments,
                removedFragments,
                fallbackRebuildFragments,
                restoredIrBytes,
                rebuiltIrBytes,
                restoredSnapshot.ModuleFingerprint,
                MatchesCurrentIr: false,
                Applied: false));
    }
}
