using Eidosc.Symbols;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class BorrowCapabilitySnapshotTests
{
    [Fact]
    public void ExplainCapabilityResolution_LocalWithoutExplicitGrant_FallsBackToGlobal()
    {
        var local = new LocalId { Value = 1 };
        var snapshot = BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read);

        var explanation = snapshot.ExplainCapabilityResolution(local, BorrowCapabilityKind.Read);

        Assert.Contains($"local:%{local.Value}->global", explanation, StringComparison.Ordinal);
        Assert.Contains("source=global", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainCapabilityResolution_ExplicitLocalGrant_MissingRequiredCapability_BlocksGlobalFallback()
    {
        var local = new LocalId { Value = 2 };
        var snapshot = BorrowCapabilitySnapshot.Enforced(
            BorrowCapabilityKind.Read,
            BorrowCapabilityKind.Write,
            BorrowCapabilityKind.Move);
        snapshot.GrantLocal(local, BorrowCapabilityKind.Read);

        var explanation = snapshot.ExplainCapabilityResolution(local, BorrowCapabilityKind.Move);

        Assert.Contains($"local:%{local.Value}", explanation, StringComparison.Ordinal);
        Assert.Contains("source=none", explanation, StringComparison.Ordinal);
        Assert.Contains("fallback=blocked-by-explicit-local", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainCapabilityResolution_ExplicitTargetGrant_MissingRequiredCapability_BlocksLocalFallback()
    {
        var target = BorrowTarget.ForLocal(new LocalId { Value = 3 });
        var snapshot = BorrowCapabilitySnapshot.Enforced(
            BorrowCapabilityKind.Read,
            BorrowCapabilityKind.Write,
            BorrowCapabilityKind.Move);
        snapshot.GrantTarget(target, BorrowCapabilityKind.Read);

        var explanation = snapshot.ExplainCapabilityResolution(target, BorrowCapabilityKind.Move);

        Assert.Contains($"target:{target.StableKey}", explanation, StringComparison.Ordinal);
        Assert.Contains("source=none", explanation, StringComparison.Ordinal);
        Assert.Contains("fallback=blocked-by-explicit-target", explanation, StringComparison.Ordinal);
    }

}
