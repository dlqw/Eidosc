using Eidosc.Mir.Closure;
using Xunit;

namespace Eidosc.Tests.Unit.Closure;

public class HandlerEnvironmentTests
{
    [Fact]
    public void Push_AddsHandlerToChain()
    {
        var env = new HandlerEnvironment();
        env.Push("handler1");

        Assert.Single(env.Chain);
        Assert.Equal("handler1", env.Chain.First());
    }

    [Fact]
    public void Pop_RemovesHandlerFromChain()
    {
        var env = new HandlerEnvironment();
        env.Push("handler1");
        env.Push("handler2");

        var popped = env.Pop();

        Assert.Equal("handler2", popped);
        Assert.Single(env.Chain);
    }

    [Fact]
    public void FindHandler_ReturnsCorrectHandler()
    {
        var env = new HandlerEnvironment();
        env.Push("handler1");
        env.Push("handler2");

        var found = env.FindHandler(h => h == "handler1");

        Assert.Equal("handler1", found);
    }

    [Fact]
    public void IsEmpty_WhenNoHandlers_ReturnsTrue()
    {
        var env = new HandlerEnvironment();

        Assert.True(env.IsEmpty);
    }

    [Fact]
    public void Depth_WithHandlers_ReturnsCorrectCount()
    {
        var env = new HandlerEnvironment();
        env.Push("h1");
        env.Push("h2");

        Assert.Equal(2, env.Depth);
    }
}
