using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class FunctionFingerprintDiffSnapshotTests
{
    [Fact]
    public void Create_ClassifiesUnchangedChangedAddedAndRemovedFunctions()
    {
        var snapshot = FunctionFingerprintDiffSnapshot.Create(
            "mir",
            "prev-module",
            "curr-module",
            [
                ("name:stable", "same"),
                ("name:changed", "old"),
                ("name:removed", "gone")
            ],
            [
                ("name:stable", "same"),
                ("name:changed", "new"),
                ("name:added", "fresh")
            ]);

        Assert.Equal("function-fingerprint-diff-snapshot-v1", snapshot.SchemaVersion);
        Assert.Equal("mir", snapshot.Kind);
        Assert.Equal("prev-module", snapshot.PreviousModuleFingerprint);
        Assert.Equal("curr-module", snapshot.CurrentModuleFingerprint);
        Assert.Equal(1, snapshot.Count(FunctionFingerprintDiffStatus.Unchanged));
        Assert.Equal(1, snapshot.Count(FunctionFingerprintDiffStatus.Changed));
        Assert.Equal(1, snapshot.Count(FunctionFingerprintDiffStatus.Added));
        Assert.Equal(1, snapshot.Count(FunctionFingerprintDiffStatus.Removed));
        Assert.Contains(snapshot.Functions, static entry =>
            entry.FunctionKey == "name:added" &&
            entry.Status == FunctionFingerprintDiffStatus.Added &&
            entry.PreviousBodyHash == "" &&
            entry.CurrentBodyHash == "fresh");
        Assert.Contains(snapshot.Functions, static entry =>
            entry.FunctionKey == "name:removed" &&
            entry.Status == FunctionFingerprintDiffStatus.Removed &&
            entry.PreviousBodyHash == "gone" &&
            entry.CurrentBodyHash == "");
    }

    [Fact]
    public void FunctionWorklist_MapsDiffStatusesToRestoreRebuildAndRemove()
    {
        var diff = FunctionFingerprintDiffSnapshot.Create(
            "mir",
            "prev-module",
            "curr-module",
            [
                ("name:stable", "same"),
                ("name:changed", "old"),
                ("name:removed", "gone")
            ],
            [
                ("name:stable", "same"),
                ("name:changed", "new"),
                ("name:added", "fresh")
            ]);

        var worklist = FunctionWorklistSnapshot.FromDiff(diff);

        Assert.Equal("function-worklist-snapshot-v1", worklist.SchemaVersion);
        Assert.Equal("mir", worklist.Kind);
        Assert.Equal(1, worklist.Count(FunctionWorklistAction.Restore));
        Assert.Equal(2, worklist.Count(FunctionWorklistAction.Rebuild));
        Assert.Equal(1, worklist.Count(FunctionWorklistAction.Remove));
        Assert.Contains(worklist.Functions, static entry =>
            entry.FunctionKey == "name:stable" &&
            entry.Action == FunctionWorklistAction.Restore);
        Assert.Contains(worklist.Functions, static entry =>
            entry.FunctionKey == "name:changed" &&
            entry.Action == FunctionWorklistAction.Rebuild);
        Assert.Contains(worklist.Functions, static entry =>
            entry.FunctionKey == "name:removed" &&
            entry.Action == FunctionWorklistAction.Remove);
    }

    [Fact]
    public void Create_DuplicateFunctionKeys_MatchesByBodyHashWithoutThrowing()
    {
        var snapshot = FunctionFingerprintDiffSnapshot.Create(
            "mir",
            "prev-module",
            "curr-module",
            [
                ("sym:Std::File::528", "same"),
                ("sym:Std::File::528", "old"),
                ("sym:Std::File::528", "removed")
            ],
            [
                ("sym:Std::File::528", "same"),
                ("sym:Std::File::528", "new"),
                ("sym:Std::File::528", "added")
            ]);

        Assert.Equal(1, snapshot.Count(FunctionFingerprintDiffStatus.Unchanged));
        Assert.Equal(2, snapshot.Count(FunctionFingerprintDiffStatus.Changed));
        Assert.Equal(0, snapshot.Count(FunctionFingerprintDiffStatus.Added));
        Assert.Equal(0, snapshot.Count(FunctionFingerprintDiffStatus.Removed));
        Assert.Contains(snapshot.Functions, static entry =>
            entry.FunctionKey == "sym:Std::File::528" &&
            entry.Status == FunctionFingerprintDiffStatus.Unchanged &&
            entry.PreviousBodyHash == "same" &&
            entry.CurrentBodyHash == "same");
    }
}
