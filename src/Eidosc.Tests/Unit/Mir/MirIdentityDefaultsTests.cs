using Eidosc;
using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class MirIdentityDefaultsTests
{
    [Fact]
    public void MirFunctionRef_DefaultSymbolId_IsNone()
    {
        var functionRef = new MirFunctionRef();

        Assert.Equal(SymbolId.None, functionRef.SymbolId);
        Assert.False(functionRef.SymbolId.IsValid);
    }

    [Fact]
    public void MirAlloc_DefaultTypeId_IsNone()
    {
        var alloc = new MirAlloc();

        Assert.Equal(TypeId.None, alloc.TypeId);
        Assert.False(alloc.TypeId.IsValid);
    }

    [Fact]
    public void FunctionIdentity_StableDeclarationKey_IgnoresSessionSymbolId()
    {
        var first = new FunctionId
        {
            SymbolId = new SymbolId(101),
            StableIdentityKey = "current@workspace::Lib\0Function\0id\0Lib.eidos\012"
        };
        var second = first with { SymbolId = new SymbolId(202) };

        Assert.True(MirFunctionIdentity.TryGetStableKey(first, out var firstKey));
        Assert.True(MirFunctionIdentity.TryGetStableKey(second, out var secondKey));
        Assert.Equal(firstKey, secondKey);
    }
}
