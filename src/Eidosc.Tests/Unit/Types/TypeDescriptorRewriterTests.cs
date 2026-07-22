using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public sealed class TypeDescriptorRewriterTests
{
    [Fact]
    public void RewriteTypeIds_Unchanged_ReturnsSameDescriptor()
    {
        var descriptor = new TypeDescriptor.Function(
            [new TypeId(1), new TypeId(2)],
            new TypeId(3),
            "io");

        var rewritten = TypeDescriptorRewriter.RewriteTypeIds(descriptor, static typeId => typeId);

        Assert.Same(descriptor, rewritten);
    }

    [Fact]
    public void RewriteTypeIds_Changed_RewritesChildTypeIds()
    {
        var descriptor = new TypeDescriptor.TyCon(
            TypeConstructorKey.FromSymbol(new SymbolId(42)),
            [new TypeId(1), new TypeId(2)])
        {
            EffectArgs = [new GenericEffectArgumentDescriptor(2, "symbol:3", new TypeId(3))]
        };

        var rewritten = Assert.IsType<TypeDescriptor.TyCon>(
            TypeDescriptorRewriter.RewriteTypeIds(
                descriptor,
                static typeId => typeId.Value switch
                {
                    1 => new TypeId(10),
                    3 => new TypeId(30),
                    _ => typeId
                }));

        Assert.NotSame(descriptor, rewritten);
        Assert.Equal(TypeConstructorKey.FromSymbol(new SymbolId(42)), rewritten.Constructor);
        Assert.Equal([new TypeId(10), new TypeId(2)], rewritten.TypeArgs);
        Assert.Equal(new TypeId(30), Assert.Single(rewritten.EffectArgs).TypeId);
    }
}
