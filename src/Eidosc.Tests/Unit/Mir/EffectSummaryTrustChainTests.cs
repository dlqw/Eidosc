using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

/// <summary>
/// A function with an HIR-inferred effect summary is a trusted fact: missing
/// summaries on callees must widen the conservative Memory/May* flags but must
/// not revoke the caller's own trust (which would poison whole call chains).
/// </summary>
public sealed class EffectSummaryTrustChainTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);

    [Fact]
    public void Analyze_CallerWithOwnPureSummary_MissingCalleeSummary_KeepsTrusted()
    {
        var callee = CreateIdentityFunction("callee", SymbolId.None);
        var caller = CreateUnusedCallFunction(callee, "caller", new SymbolId(201));
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var summaries = CreateSummaries(
            (caller.SymbolId, EffectRow.Pure));

        var index = FunctionOptimizationSummaryAnalyzer.Analyze(module, summaries);

        Assert.True(index.TryGet(CreateFunctionRef(caller), out var summary));
        Assert.True(summary.IsTrusted);
        Assert.True(summary.Effects.IsPure);
        Assert.Equal(FunctionMemoryBehavior.Unknown, summary.Memory);
        Assert.True(summary.MayPanic);
        Assert.False(summary.CanEliminateUnusedCall);
        Assert.False(summary.CanReuseCallResult);
    }

    [Fact]
    public void Analyze_TwoLevelChainWithOwnSummaries_MissingLeaf_WidensMemoryOnly()
    {
        var leaf = CreateIdentityFunction("leaf", SymbolId.None);
        var middle = CreateUnusedCallFunction(leaf, "middle", new SymbolId(202));
        var caller = CreateUnusedCallFunction(middle, "caller", new SymbolId(203));
        var module = new MirModule { Name = "Main", Functions = [leaf, middle, caller] };
        var summaries = CreateSummaries(
            (middle.SymbolId, EffectRow.Pure),
            (caller.SymbolId, EffectRow.Pure));

        var index = FunctionOptimizationSummaryAnalyzer.Analyze(module, summaries);

        Assert.True(index.TryGet(CreateFunctionRef(caller), out var callerSummary));
        Assert.True(callerSummary.IsTrusted);
        Assert.True(callerSummary.Effects.IsPure);
        Assert.Equal(FunctionMemoryBehavior.Unknown, callerSummary.Memory);
        Assert.False(callerSummary.CanEliminateUnusedCall);

        Assert.True(index.TryGet(CreateFunctionRef(middle), out var middleSummary));
        Assert.True(middleSummary.IsTrusted);
        Assert.True(middleSummary.Effects.IsPure);
        Assert.Equal(FunctionMemoryBehavior.Unknown, middleSummary.Memory);
        Assert.False(middleSummary.CanEliminateUnusedCall);
    }

    [Fact]
    public void Analyze_CallerWithoutOwnSummary_StaysUntrusted()
    {
        var callee = CreateIdentityFunction("callee", new SymbolId(204));
        var caller = CreateUnusedCallFunction(callee, "caller", SymbolId.None);
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var summaries = CreateSummaries(
            (callee.SymbolId, EffectRow.Pure));

        var index = FunctionOptimizationSummaryAnalyzer.Analyze(module, summaries);

        Assert.True(index.TryGet(CreateFunctionRef(caller), out var summary));
        Assert.False(summary.IsTrusted);
        Assert.False(summary.CanEliminateUnusedCall);
    }

    private static Dictionary<SymbolId, FunctionEffectSummary> CreateSummaries(
        params (SymbolId SymbolId, EffectRow Effects)[] bindings) => bindings.ToDictionary(
        static binding => binding.SymbolId,
        static binding => new FunctionEffectSummary(binding.Effects, binding.Effects));

    private static MirFunc CreateIdentityFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "value", isParameter: true);
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" },
            Locals = [parameter],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Terminator = new MirReturn { Value = Place(parameter.Id) }
                }
            ]
        };
    }

    private static MirFunc CreateUnusedCallFunction(MirFunc callee, string name, SymbolId symbolId)
    {
        var argument = Local(1, "argument", isParameter: true);
        var result = Local(2, "result");
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" },
            Locals = [argument, result],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [Call(result.Id, CreateFunctionRef(callee), Place(argument.Id))],
                    Terminator = new MirReturn()
                }
            ]
        };
    }

    private static MirCall Call(LocalId target, MirFunctionRef function, params MirOperand[] arguments) => new()
    {
        Target = Place(target),
        Function = function,
        Arguments = [.. arguments]
    };

    private static MirFunctionRef CreateFunctionRef(MirFunc function) => new()
    {
        Name = function.Name,
        SymbolId = function.SymbolId,
        FunctionId = function.FunctionId
    };

    private static MirLocal Local(int id, string name, bool isParameter = false) => new()
    {
        Id = new LocalId { Value = id },
        Name = name,
        TypeId = IntType,
        IsParameter = isParameter
    };

    private static MirPlace Place(LocalId local) => new()
    {
        Kind = PlaceKind.Local,
        Local = local,
        TypeId = IntType
    };

    private static BlockId Block(int value) => new() { Value = value };
}
