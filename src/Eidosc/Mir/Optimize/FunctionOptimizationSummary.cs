using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

[Flags]
public enum FunctionMemoryBehavior
{
    None = 0,
    Read = 1,
    Write = 2,
    Unknown = 4
}

public enum FunctionDeterminism
{
    Deterministic,
    Nondeterministic,
    Unknown
}

public sealed record FunctionOptimizationSummary(
    EffectRow Effects,
    FunctionMemoryBehavior Memory,
    bool MayPanic,
    bool MayDiverge,
    bool MaySuspend,
    bool MayBlock,
    bool MayAllocate,
    bool MaySynchronize,
    FunctionDeterminism Determinism,
    bool IsTrusted)
{
    public static FunctionOptimizationSummary Pure { get; } = new(
        EffectRow.Pure,
        FunctionMemoryBehavior.None,
        false,
        false,
        false,
        false,
        false,
        false,
        FunctionDeterminism.Deterministic,
        true);

    public static FunctionOptimizationSummary Unknown { get; } = new(
        EffectRow.Pure,
        FunctionMemoryBehavior.Unknown,
        true,
        true,
        true,
        true,
        true,
        true,
        FunctionDeterminism.Unknown,
        false);

    public static FunctionOptimizationSummary FromTrustedEffects(EffectRow effects) => effects.IsPure
        ? Pure
        : new FunctionOptimizationSummary(
            effects,
            FunctionMemoryBehavior.Unknown,
            true,
            true,
            true,
            true,
            true,
            true,
            FunctionDeterminism.Unknown,
            true);

    public bool CanEliminateUnusedCall =>
        IsTrusted &&
        Effects.IsPure &&
        Memory == FunctionMemoryBehavior.None &&
        !MayPanic &&
        !MayDiverge &&
        !MaySuspend &&
        !MayBlock &&
        !MayAllocate &&
        !MaySynchronize;

    public bool CanReuseCallResult =>
        CanEliminateUnusedCall && Determinism == FunctionDeterminism.Deterministic;

    public FunctionOptimizationSummary Join(FunctionOptimizationSummary other) => new(
        Effects.Union(other.Effects),
        JoinMemory(Memory, other.Memory),
        MayPanic || other.MayPanic,
        MayDiverge || other.MayDiverge,
        MaySuspend || other.MaySuspend,
        MayBlock || other.MayBlock,
        MayAllocate || other.MayAllocate,
        MaySynchronize || other.MaySynchronize,
        JoinDeterminism(Determinism, other.Determinism),
        IsTrusted && other.IsTrusted);

    private static FunctionMemoryBehavior JoinMemory(
        FunctionMemoryBehavior left,
        FunctionMemoryBehavior right) =>
        left.HasFlag(FunctionMemoryBehavior.Unknown) || right.HasFlag(FunctionMemoryBehavior.Unknown)
            ? FunctionMemoryBehavior.Unknown
            : left | right;

    private static FunctionDeterminism JoinDeterminism(
        FunctionDeterminism left,
        FunctionDeterminism right)
    {
        if (left == FunctionDeterminism.Nondeterministic || right == FunctionDeterminism.Nondeterministic)
        {
            return FunctionDeterminism.Nondeterministic;
        }

        return left == FunctionDeterminism.Deterministic && right == FunctionDeterminism.Deterministic
            ? FunctionDeterminism.Deterministic
            : FunctionDeterminism.Unknown;
    }
}

public sealed class FunctionOptimizationSummaryIndex
{
    private readonly IReadOnlyDictionary<string, FunctionOptimizationSummary> _byFunctionKey;

    internal FunctionOptimizationSummaryIndex(
        IReadOnlyDictionary<string, FunctionOptimizationSummary> byFunctionKey)
    {
        _byFunctionKey = byFunctionKey;
    }

    public IReadOnlyDictionary<string, FunctionOptimizationSummary> Summaries => _byFunctionKey;

    public bool TryGet(MirFunctionRef function, out FunctionOptimizationSummary summary) =>
        _byFunctionKey.TryGetValue(MirFunctionIdentity.GetStableKey(function), out summary!);
}

public static class FunctionOptimizationSummaryAnalyzer
{
    public static FunctionOptimizationSummaryIndex Analyze(
        MirModule module,
        IReadOnlyDictionary<SymbolId, FunctionEffectSummary>? effectSummaries = null)
    {
        var functions = module.Functions
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var local = new Dictionary<string, FunctionOptimizationSummary>(StringComparer.Ordinal);
        var calls = new Dictionary<string, List<CallEdge>>(StringComparer.Ordinal);

        foreach (var (key, function) in functions)
        {
            FunctionEffectSummary? effectSummary = null;
            var hasEffectSummary = function.SymbolId.IsValid &&
                                   effectSummaries != null &&
                                   effectSummaries.TryGetValue(function.SymbolId, out effectSummary);
            var summary = function.IsExternal
                ? FunctionOptimizationSummary.Unknown with
                {
                    Effects = hasEffectSummary ? effectSummary!.InferredEffects : EffectRow.Pure
                }
                : hasEffectSummary
                    ? FunctionOptimizationSummary.FromTrustedEffects(effectSummary!.InferredEffects)
                    : FunctionOptimizationSummary.Unknown;
            var functionCalls = new List<CallEdge>();
            foreach (var block in function.BasicBlocks)
            {
                if (block.Terminator is MirUnreachable)
                {
                    summary = summary with { MayDiverge = true };
                }

                foreach (var instruction in block.Instructions)
                {
                    switch (instruction)
                    {
                        case MirLoad:
                            summary = summary with
                            {
                                Memory = JoinMemory(summary.Memory, FunctionMemoryBehavior.Read)
                            };
                            break;
                        case MirStore or MirDrop:
                            summary = summary with
                            {
                                Memory = JoinMemory(summary.Memory, FunctionMemoryBehavior.Write)
                            };
                            break;
                        case MirAlloc:
                            summary = summary with { MayAllocate = true };
                            break;
                        case MirBinOp { Operator: BinaryOp.Div or BinaryOp.Mod }:
                            summary = summary with { MayPanic = true };
                            break;
                        case MirBinOp { Operator: BinaryOp.Concat }:
                            summary = summary with { MayAllocate = true };
                            break;
                        case MirCall { Function: MirFunctionRef functionRef } call:
                            var calleeKey = MirFunctionIdentity.GetStableKey(functionRef);
                            var isPartial = functions.TryGetValue(calleeKey, out var callee) &&
                                            call.Arguments.Count < callee.Locals.Count(static local => local.IsParameter);
                            functionCalls.Add(new CallEdge(calleeKey, isPartial));
                            if (isPartial)
                            {
                                summary = summary with { MayAllocate = true };
                            }
                            break;
                        case MirCall:
                            summary = summary.Join(FunctionOptimizationSummary.Unknown);
                            break;
                    }
                }
            }

            local[key] = summary;
            calls[key] = functionCalls;
        }

        foreach (var component in RecursiveCallAnalysis.Analyze(module).Components)
        {
            foreach (var key in component.FunctionKeys)
            {
                if (local.TryGetValue(key, out var summary))
                {
                    local[key] = summary with { MayDiverge = true };
                }
            }
        }

        var result = new Dictionary<string, FunctionOptimizationSummary>(local, StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var key in functions.Keys.OrderBy(static value => value, StringComparer.Ordinal))
            {
                var summary = local[key];
                foreach (var call in calls[key])
                {
                    if (call.IsPartial)
                    {
                        continue;
                    }

                    summary = summary.Join(result.GetValueOrDefault(
                        call.CalleeKey,
                        FunctionOptimizationSummary.Unknown));
                }

                if (summary == result[key])
                {
                    continue;
                }

                result[key] = summary;
                changed = true;
            }
        }

        return new FunctionOptimizationSummaryIndex(result);
    }

    private static FunctionMemoryBehavior JoinMemory(
        FunctionMemoryBehavior left,
        FunctionMemoryBehavior right) =>
        left.HasFlag(FunctionMemoryBehavior.Unknown) || right.HasFlag(FunctionMemoryBehavior.Unknown)
            ? FunctionMemoryBehavior.Unknown
            : left | right;

    private readonly record struct CallEdge(string CalleeKey, bool IsPartial);
}

internal interface IFunctionOptimizationSummaryConsumer
{
    FunctionOptimizationSummaryIndex FunctionSummaries { set; }
}
