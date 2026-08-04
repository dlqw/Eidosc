using Eidosc.Mir;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class RuntimeSequenceBuildLoweringTests
{
    [Fact]
    public void EstimateComprehensionCapacity_KnownSources_ComputesExactProduct()
    {
        var estimate = RuntimeSequenceBuildLowering.EstimateComprehensionCapacity([3, 4], hasGuard: false);

        Assert.Equal(12, estimate.InitialCapacity);
        Assert.Equal(12, estimate.UpperBound);
        Assert.True(estimate.IsExact);
    }

    [Fact]
    public void EstimateComprehensionCapacity_Guard_KeepsUpperBoundWithoutClaimingExactLength()
    {
        var estimate = RuntimeSequenceBuildLowering.EstimateComprehensionCapacity([5], hasGuard: true);

        Assert.Equal(5, estimate.InitialCapacity);
        Assert.Equal(5, estimate.UpperBound);
        Assert.False(estimate.IsExact);
    }

    [Fact]
    public void EstimateComprehensionCapacity_UnknownSource_UsesCompilerGrowthPolicy()
    {
        var estimate = RuntimeSequenceBuildLowering.EstimateComprehensionCapacity([3, null], hasGuard: false);

        Assert.Equal(RuntimeSequenceBuildLowering.DefaultUnknownCapacity, estimate.InitialCapacity);
        Assert.Null(estimate.UpperBound);
        Assert.False(estimate.IsExact);
    }

    [Fact]
    public void EstimateComprehensionCapacity_LargeProduct_SaturatesWithoutOverflow()
    {
        var estimate = RuntimeSequenceBuildLowering.EstimateComprehensionCapacity(
            [int.MaxValue, int.MaxValue],
            hasGuard: false);

        Assert.Equal(int.MaxValue, estimate.InitialCapacity);
        Assert.Equal(int.MaxValue, estimate.UpperBound);
        Assert.True(estimate.IsExact);
    }
}
