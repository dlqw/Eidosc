using Eidosc.Mir.Closure;
using Xunit;

namespace Eidosc.Tests.Unit.Closure;

public class NestedClosureTests
{
    [Fact]
    public void Flatten_NestedClosure_FlattensCaptureChain()
    {
        var flattener = new NestedClosureFlattener();
        flattener.AddCapture("inner", ["x", "y", "z"]);
        flattener.AddCapture("middle", ["x", "y"]);

        var result = flattener.Flatten("inner");

        Assert.Equal(3, result.CapturedVariables.Count);
        Assert.True(result.IsFlattened);
    }

    [Fact]
    public void Flatten_UnknownClosure_ReturnsEmpty()
    {
        var flattener = new NestedClosureFlattener();

        var result = flattener.Flatten("unknown");

        Assert.Empty(result.CapturedVariables);
        Assert.False(result.IsFlattened);
    }

    [Fact]
    public void AddCapture_SingleClosure_TracksCorrectly()
    {
        var flattener = new NestedClosureFlattener();
        flattener.AddCapture("my_closure", ["a", "b"]);

        var result = flattener.Flatten("my_closure");

        Assert.Contains("a", result.CapturedVariables);
        Assert.Contains("b", result.CapturedVariables);
    }
}
