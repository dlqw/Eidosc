using Eidosc;
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

    [Theory]
    [InlineData("i24", LiteralTypeSuffix.IntArbitrary, 24)]
    [InlineData("I24", LiteralTypeSuffix.IntArbitrary, 24)]
    [InlineData("u512", LiteralTypeSuffix.UIntArbitrary, 512)]
    [InlineData("U1", LiteralTypeSuffix.UIntArbitrary, 1)]
    public void LiteralSuffixTable_MatchesArbitraryWidth(string suffixText, LiteralTypeSuffix expectedSuffix, int expectedWidth)
    {
        Assert.True(LiteralSuffixTable.TryMatch(suffixText, 0, out var suffix, out var length, out var width));
        Assert.Equal(expectedSuffix, suffix);
        Assert.Equal(suffixText.Length, length);
        Assert.Equal(expectedWidth, width);
    }

    [Fact]
    public void LiteralSuffixTable_IntegerPredicates_IncludeArbitraryWidths()
    {
        Assert.True(LiteralSuffixTable.IsInteger(LiteralTypeSuffix.IntArbitrary));
        Assert.True(LiteralSuffixTable.IsInteger(LiteralTypeSuffix.UIntArbitrary));
        Assert.True(LiteralSuffixTable.IsSigned(LiteralTypeSuffix.IntArbitrary));
        Assert.False(LiteralSuffixTable.IsSigned(LiteralTypeSuffix.UIntArbitrary));
        Assert.False(LiteralSuffixTable.IsFloat(LiteralTypeSuffix.IntArbitrary));
    }

    [Theory]
    [InlineData(false, 24, 0x7FFFFFUL)]
    [InlineData(true, 24, 0xFFFFFFUL)]
    [InlineData(false, 1, 1UL)]
    [InlineData(true, 1, 1UL)]
    public void ArbitraryWidthMagnitudeLimit_MatchesWidth(bool unsigned, int width, ulong expectedMax)
    {
        var suffix = unsigned ? LiteralTypeSuffix.UIntArbitrary : LiteralTypeSuffix.IntArbitrary;
        Assert.True(LiteralSuffixTable.TryGetMagnitudeLimit(suffix, width, out var max));
        Assert.Equal(expectedMax, max);
    }

    [Fact]
    public void IntegerTypeWidths_OutsideSupportedRange_AreRejected()
    {
        Assert.False(BaseTypes.TryParseIntegerTypeName("I0", out _, out _));
        Assert.False(BaseTypes.TryParseIntegerTypeName("U4097", out _, out _));
        Assert.Equal(TypeId.None, BaseTypes.GetIntegerTypeId(unsigned: false, width: 4097));
        Assert.Equal(TypeId.None, BaseTypes.GetIntegerTypeId(unsigned: true, width: 0));
        Assert.Equal(TypeId.None, BaseTypes.GetIntegerTypeId(unsigned: false, width: 1_000_000));
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
