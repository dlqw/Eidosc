using Eidosc.Bindgen;

namespace Eidosc.Tests.Unit.Bindgen;

public sealed class BindingTypeMapperTests
{
    private static readonly CHeaderIr EmptyHeader = new("test.h", [], [], []);

    [Fact]
    public void Map_SignedInt64Typedef_UsesIdiomaticInt()
    {
        var mapper = new BindingTypeMapper(EmptyHeader);

        var mapping = mapper.Map(new CBindingType(
            CBindingTypeKind.Typedef,
            "int64_t",
            "int64_t"));

        Assert.Equal("Int", mapping.EidosType);
        Assert.Equal(BindingTypeCategory.Direct, mapping.Category);
    }

    [Fact]
    public void Map_UnsignedInt64Typedef_PreservesFixedWidthBoundary()
    {
        var mapper = new BindingTypeMapper(EmptyHeader);

        var mapping = mapper.Map(new CBindingType(
            CBindingTypeKind.Typedef,
            "uint64_t",
            "uint64_t",
            IsUnsigned: true));

        Assert.Equal("UInt64", mapping.EidosType);
        Assert.Equal(BindingTypeCategory.Direct, mapping.Category);
    }
}
