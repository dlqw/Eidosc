using Eidosc.Mir.Closure;
using Xunit;

namespace Eidosc.Tests.Unit.Closure;

public class RecursiveClosureTests
{
    [Fact]
    public void DetectSelfReference_WithSelfReference_ReturnsTrue()
    {
        var resolver = new RecursiveClosureResolver();
        var capturedVars = new[] { "self", "x", "y" };

        var result = resolver.DetectSelfReference("self", capturedVars);

        Assert.True(result);
    }

    [Fact]
    public void DetectSelfReference_WithoutSelfReference_ReturnsFalse()
    {
        var resolver = new RecursiveClosureResolver();
        var capturedVars = new[] { "x", "y" };

        var result = resolver.DetectSelfReference("self", capturedVars);

        Assert.False(result);
    }

    [Fact]
    public void CreateFixPoint_ReturnsCorrectResult()
    {
        var resolver = new RecursiveClosureResolver();
        var result = resolver.CreateFixPoint("factorial");

        Assert.Equal("factorial", result.FunctionName);
        Assert.True(result.RequiresFixPoint);
    }
}
