namespace Eidosc.Tests.Unit.Mir;

using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

public class StaticBranchRegistryTests
{
    [Fact]
    public void Register_AndResolve_ByHandlerName()
    {
        var registry = new StaticBranchRegistry();
        registry.Register("h", "Emitter", "emit", new StaticBranchInfo("eidos___handler_1_emit", new TypeId(BaseTypes.IntId)));

        Assert.True(registry.TryResolve("h", "emit", out var info));
        Assert.Equal("eidos___handler_1_emit", info.BranchFunctionName);
    }

    [Fact]
    public void TryResolve_UnknownHandler_ReturnsFalse()
    {
        var registry = new StaticBranchRegistry();
        Assert.False(registry.TryResolve("unknown", "emit", out _));
    }

    [Fact]
    public void TryResolve_UnknownOperation_ReturnsFalse()
    {
        var registry = new StaticBranchRegistry();
        registry.Register("h", "Emitter", "emit", new StaticBranchInfo("branch_fn", TypeId.None));
        Assert.False(registry.TryResolve("h", "missing", out _));
    }

    [Fact]
    public void Register_MultipleHandlers_SameOperation_DisambiguatedByName()
    {
        var registry = new StaticBranchRegistry();
        registry.Register("h1", "Emitter", "emit", new StaticBranchInfo("branch_1", TypeId.None));
        registry.Register("h2", "Emitter", "emit", new StaticBranchInfo("branch_2", TypeId.None));

        Assert.True(registry.TryResolve("h1", "emit", out var info1));
        Assert.Equal("branch_1", info1.BranchFunctionName);
        Assert.True(registry.TryResolve("h2", "emit", out var info2));
        Assert.Equal("branch_2", info2.BranchFunctionName);
    }
}
