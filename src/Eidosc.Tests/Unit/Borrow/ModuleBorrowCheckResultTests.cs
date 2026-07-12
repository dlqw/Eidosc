using Eidosc.Symbols;
using Eidosc.Borrow;
using Eidosc.Semantic;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public sealed class ModuleBorrowCheckResultTests
{
    [Fact]
    public void AddResult_SymbolBackedFunctions_UsesStructuredFunctionKeyAsPrimaryIndex()
    {
        var firstSymbol = new SymbolId(101);
        var secondSymbol = new SymbolId(202);
        var first = CreateResult("duplicate", firstSymbol);
        var second = CreateResult("duplicate", secondSymbol);
        var moduleResult = new ModuleBorrowCheckResult();

        moduleResult.AddResult(first);
        moduleResult.AddResult(second);

        Assert.Equal(2, moduleResult.ResultsByFunctionKey.Count);
        Assert.True(moduleResult.ResultsByFunctionKey.ContainsKey(BorrowFunctionKey.From("duplicate", firstSymbol)));
        Assert.True(moduleResult.ResultsByFunctionKey.ContainsKey(BorrowFunctionKey.From("duplicate", secondSymbol)));
        Assert.True(moduleResult.TryGetFunctionResult(firstSymbol, "duplicate", out var firstLookup));
        Assert.Same(first, firstLookup);
        Assert.True(moduleResult.TryGetFunctionResult(secondSymbol, "duplicate", out var secondLookup));
        Assert.Same(second, secondLookup);
        Assert.False(moduleResult.TryGetFunctionResult(SymbolId.None, "duplicate", out _));
    }

    [Fact]
    public void AddResult_NameOnlyDuplicateFunctions_KeepsCompatibilityKeysButRejectsAmbiguousNameLookup()
    {
        var first = CreateResult("duplicate", SymbolId.None);
        var second = CreateResult("duplicate", SymbolId.None);
        var moduleResult = new ModuleBorrowCheckResult();

        moduleResult.AddResult(first);
        moduleResult.AddResult(second);

        Assert.Equal(2, moduleResult.ResultsByFunctionKey.Count);
        Assert.Contains("name:duplicate", moduleResult.FunctionResults.Keys);
        Assert.Contains("name:duplicate#1", moduleResult.FunctionResults.Keys);
        Assert.False(moduleResult.TryGetFunctionResult(SymbolId.None, "duplicate", out _));
    }

    [Fact]
    public void TryGetFunctionResult_StructuredKey_ReturnsMatchingResult()
    {
        var symbol = new SymbolId(303);
        var result = CreateResult("target", symbol);
        var key = BorrowFunctionKey.From("target", symbol);
        var moduleResult = new ModuleBorrowCheckResult();

        moduleResult.AddResult(result);

        Assert.True(moduleResult.TryGetFunctionResult(key, out var lookup));
        Assert.Same(result, lookup);
        Assert.Equal("sym:303", key.StableText);
    }

    private static BorrowCheckResult CreateResult(string name, SymbolId symbolId)
    {
        return new BorrowCheckResult
        {
            FunctionName = name,
            FunctionSymbolId = symbolId
        };
    }
}
