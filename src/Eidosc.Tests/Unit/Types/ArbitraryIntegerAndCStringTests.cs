using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class ArbitraryIntegerAndCStringTests
{
    [Theory]
    [InlineData("I24", false, 24)]
    [InlineData("I512", false, 512)]
    [InlineData("U8", true, 8)]
    [InlineData("U512", true, 512)]
    public void ParseIntegerTypeName_RecognizesIUPrefix(string name, bool expectedUnsigned, int expectedWidth)
    {
        Assert.True(BaseTypes.TryParseIntegerTypeName(name, out var unsigned, out var width));
        Assert.Equal(expectedUnsigned, unsigned);
        Assert.Equal(expectedWidth, width);
    }

    [Theory]
    [InlineData("I24", false, 24)]
    [InlineData("U24", true, 24)]
    [InlineData("I512", false, 512)]
    public void IntegerTypeId_RoundTrips(string name, bool expectedUnsigned, int expectedWidth)
    {
        var typeId = BaseTypes.GetBuiltInTypeId(name);
        Assert.True(typeId.IsValid);
        Assert.True(BaseTypes.TryGetIntegerTypeInfo(typeId, out var unsigned, out var width));
        Assert.Equal(expectedUnsigned, unsigned);
        Assert.Equal(expectedWidth, width);
    }

    [Fact]
    public void OneBitInteger_MapsToBool()
    {
        Assert.Equal(BaseTypes.BoolId, BaseTypes.GetIntegerTypeId(unsigned: false, width: 1).Value);
        Assert.Equal(BaseTypes.BoolId, BaseTypes.GetIntegerTypeId(unsigned: true, width: 1).Value);
    }

    [Fact]
    public void ArbitraryInteger_HasNumericTrait()
    {
        Assert.True(BuiltinTraits.HasTrait("I24", BuiltinTraits.TraitNames.Num));
        Assert.True(BuiltinTraits.HasTrait("U512", BuiltinTraits.TraitNames.Num));
        Assert.True(BuiltinTraits.HasTrait("I24", BuiltinTraits.TraitNames.Eq));
        Assert.True(BuiltinTraits.HasTrait("U512", BuiltinTraits.TraitNames.Ord));
    }

    [Fact]
    public void CString_IsBuiltInAndIntegerParsingRejectsPlainI()
    {
        var cStringId = BaseTypes.GetBuiltInTypeId("CString");
        Assert.True(cStringId.IsValid);
        Assert.Equal(BaseTypes.CStringId, cStringId.Value);
        Assert.False(BaseTypes.TryParseIntegerTypeName("I", out _, out _));
        Assert.False(BaseTypes.TryParseIntegerTypeName("U", out _, out _));
    }
}
