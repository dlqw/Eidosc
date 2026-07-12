using Eidosc.Mir.Closure;
using Xunit;

namespace Eidosc.Tests.Unit.Closure;

public class MutualRecursionTests
{
    [Fact]
    public void Detect_MutuallyRecursive_ReturnsSCC()
    {
        var detector = new MutualRecursionDetector();
        detector.AddCall("is_even", "is_odd");
        detector.AddCall("is_odd", "is_even");

        var sccs = detector.FindStronglyConnectedComponents();

        Assert.Single(sccs);
        Assert.Equal(2, sccs[0].Count);
    }

    [Fact]
    public void Detect_NoRecursion_ReturnsEmpty()
    {
        var detector = new MutualRecursionDetector();
        detector.AddCall("a", "b");
        detector.AddCall("b", "c");

        var sccs = detector.FindStronglyConnectedComponents();

        Assert.Empty(sccs);
    }

    [Fact]
    public void Detect_SelfRecursive_ReturnsEmpty()
    {
        var detector = new MutualRecursionDetector();
        detector.AddCall("factorial", "factorial");

        var sccs = detector.FindStronglyConnectedComponents();

        // 自递归不算相互递归
        Assert.Empty(sccs);
    }
}
