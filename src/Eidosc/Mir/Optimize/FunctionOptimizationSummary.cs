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

[Flags]
internal enum FunctionOptimizationRequirement
{
    None = 0,
    Trusted = 1 << 0,
    PureEffects = 1 << 1,
    NoMemoryAccess = 1 << 2,
    NoPanic = 1 << 3,
    NoDivergence = 1 << 4,
    NoSuspend = 1 << 5,
    NoBlock = 1 << 6,
    NoAllocation = 1 << 7,
    NoSynchronization = 1 << 8,
    Deterministic = 1 << 9
}

internal enum FunctionOptimizationCapability
{
    EliminateUnusedCall,
    ReuseCallResult,
    ReassociatePureCalls,
    InlineBody,
    FoldConstantCall,
    ReorderSequenceCallback,
    InlineSequenceCallback,
    ElideSequenceIntermediate
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

    /// <summary>
    /// Builds the trusted summary from the full HIR contract: the declared
    /// upper bound (the <c>need</c> clause) is unioned with the inferred
    /// effects. A function that declares an effect must stay effectful even
    /// when its body infers nothing (e.g. an empty body), otherwise DCE would
    /// wrongly treat declared-effect calls as eliminable.
    /// </summary>
    public static FunctionOptimizationSummary FromTrustedEffects(FunctionEffectSummary summary) =>
        FromTrustedEffects(summary.DeclaredUpperBound.Union(summary.InferredEffects));

    public bool CanEliminateUnusedCall =>
        Allows(FunctionOptimizationCapability.EliminateUnusedCall);

    public bool CanReuseCallResult =>
        Allows(FunctionOptimizationCapability.ReuseCallResult);

    /// <summary>
    /// Whether a structurally proven recurrence may regroup repeated calls and
    /// additions. <see cref="MayDiverge"/> is intentionally omitted because the
    /// recurrence matcher is responsible for proving its own decreasing base
    /// case; the generic recursive-call analysis conservatively marks every
    /// recursive component as potentially divergent.
    /// </summary>
    public bool CanReassociatePureCalls =>
        Allows(FunctionOptimizationCapability.ReassociatePureCalls);

    /// <summary>
    /// Whether the call boundary may be replaced by the exact MIR body. The
    /// body preserves memory, allocation, panic and determinism behavior, but a
    /// declared effect cannot be represented after the call instruction is
    /// removed and therefore requires a trusted pure effect row.
    /// </summary>
    public bool CanInlineBody => Allows(FunctionOptimizationCapability.InlineBody);

    /// <summary>
    /// Whether a call with all-constant arguments can be folded at compile time.
    /// Deliberately omits <see cref="MayDiverge"/>: recursive calls are bounded by
    /// the folding evaluator's depth/step limits instead of a summary flag.
    /// </summary>
    public bool CanFoldConstantCall =>
        Allows(FunctionOptimizationCapability.FoldConstantCall);

    internal bool Allows(FunctionOptimizationCapability capability) =>
        Satisfies(GetRequirements(capability));

    internal bool Satisfies(FunctionOptimizationRequirement requirements)
    {
        if ((requirements & FunctionOptimizationRequirement.Trusted) != 0 && !IsTrusted)
        {
            return false;
        }

        if ((requirements & FunctionOptimizationRequirement.PureEffects) != 0 && !Effects.IsPure)
        {
            return false;
        }

        if ((requirements & FunctionOptimizationRequirement.NoMemoryAccess) != 0 &&
            Memory != FunctionMemoryBehavior.None)
        {
            return false;
        }

        return ((requirements & FunctionOptimizationRequirement.NoPanic) == 0 || !MayPanic) &&
               ((requirements & FunctionOptimizationRequirement.NoDivergence) == 0 || !MayDiverge) &&
               ((requirements & FunctionOptimizationRequirement.NoSuspend) == 0 || !MaySuspend) &&
               ((requirements & FunctionOptimizationRequirement.NoBlock) == 0 || !MayBlock) &&
               ((requirements & FunctionOptimizationRequirement.NoAllocation) == 0 || !MayAllocate) &&
               ((requirements & FunctionOptimizationRequirement.NoSynchronization) == 0 || !MaySynchronize) &&
               ((requirements & FunctionOptimizationRequirement.Deterministic) == 0 ||
                Determinism == FunctionDeterminism.Deterministic);
    }

    private static FunctionOptimizationRequirement GetRequirements(
        FunctionOptimizationCapability capability)
    {
        const FunctionOptimizationRequirement trustedPure =
            FunctionOptimizationRequirement.Trusted |
            FunctionOptimizationRequirement.PureEffects;
        const FunctionOptimizationRequirement observableFree =
            trustedPure |
            FunctionOptimizationRequirement.NoMemoryAccess |
            FunctionOptimizationRequirement.NoPanic |
            FunctionOptimizationRequirement.NoSuspend |
            FunctionOptimizationRequirement.NoBlock |
            FunctionOptimizationRequirement.NoAllocation |
            FunctionOptimizationRequirement.NoSynchronization;

        return capability switch
        {
            FunctionOptimizationCapability.EliminateUnusedCall =>
                observableFree | FunctionOptimizationRequirement.NoDivergence,
            FunctionOptimizationCapability.ReuseCallResult =>
                observableFree |
                FunctionOptimizationRequirement.NoDivergence |
                FunctionOptimizationRequirement.Deterministic,
            FunctionOptimizationCapability.ReassociatePureCalls =>
                observableFree | FunctionOptimizationRequirement.Deterministic,
            FunctionOptimizationCapability.InlineBody => trustedPure,
            FunctionOptimizationCapability.FoldConstantCall =>
                observableFree | FunctionOptimizationRequirement.Deterministic,
            FunctionOptimizationCapability.ReorderSequenceCallback =>
                observableFree |
                FunctionOptimizationRequirement.NoDivergence |
                FunctionOptimizationRequirement.Deterministic,
            FunctionOptimizationCapability.InlineSequenceCallback => trustedPure,
            FunctionOptimizationCapability.ElideSequenceIntermediate =>
                FunctionOptimizationRequirement.Trusted,
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
    }

    /// <summary>
    /// Joins callee knowledge while keeping this function's own trust status:
    /// a function with an HIR-inferred effect summary stays trusted even when a
    /// callee lacks a summary (missing summaries only widen the conservative
    /// Memory/May* flags, which still gate elimination and reuse).
    /// </summary>
    public FunctionOptimizationSummary JoinEffects(FunctionOptimizationSummary other) => new(
        Effects.Union(other.Effects),
        JoinMemory(Memory, other.Memory),
        MayPanic || other.MayPanic,
        MayDiverge || other.MayDiverge,
        MaySuspend || other.MaySuspend,
        MayBlock || other.MayBlock,
        MayAllocate || other.MayAllocate,
        MaySynchronize || other.MaySynchronize,
        JoinDeterminism(Determinism, other.Determinism),
        IsTrusted);

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

    public static FunctionOptimizationSummaryIndex Empty { get; } = new(
        new Dictionary<string, FunctionOptimizationSummary>(StringComparer.Ordinal));

    internal FunctionOptimizationSummaryIndex(
        IReadOnlyDictionary<string, FunctionOptimizationSummary> byFunctionKey)
    {
        _byFunctionKey = byFunctionKey;
    }

    public IReadOnlyDictionary<string, FunctionOptimizationSummary> Summaries => _byFunctionKey;

    public bool TryGet(MirFunctionRef function, out FunctionOptimizationSummary summary) =>
        _byFunctionKey.TryGetValue(MirFunctionIdentity.GetStableKey(function), out summary!);

    public bool TryGet(MirFunc function, out FunctionOptimizationSummary summary) =>
        _byFunctionKey.TryGetValue(MirFunctionIdentity.GetStableKey(function), out summary!);
}

internal sealed class FunctionOptimizationProofIndex
{
    private readonly FunctionOptimizationSummaryIndex _functionSummaries;
    private readonly HashSet<string> _recursiveFunctionKeys;

    public static FunctionOptimizationProofIndex Empty { get; } = new(
        FunctionOptimizationSummaryIndex.Empty,
        new RecursiveCallAnalysisResult { Components = [] });

    internal FunctionOptimizationProofIndex(
        FunctionOptimizationSummaryIndex functionSummaries,
        RecursiveCallAnalysisResult recursiveCalls)
    {
        _functionSummaries = functionSummaries;
        _recursiveFunctionKeys = recursiveCalls.Components
            .SelectMany(static component => component.FunctionKeys)
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool Allows(MirFunctionRef function, FunctionOptimizationCapability capability) =>
        _functionSummaries.TryGet(function, out var summary) && summary.Allows(capability);

    public bool Allows(MirFunc function, FunctionOptimizationCapability capability) =>
        _functionSummaries.TryGet(function, out var summary) && summary.Allows(capability);

    public bool TryGetSummary(MirFunctionRef function, out FunctionOptimizationSummary summary) =>
        _functionSummaries.TryGet(function, out summary!);

    public bool IsRecursive(MirFunc function) =>
        _recursiveFunctionKeys.Contains(MirFunctionIdentity.GetStableKey(function));
}

internal static class FunctionOptimizationProofAnalyzer
{
    public static FunctionOptimizationProofIndex Analyze(
        MirModule module,
        IReadOnlyDictionary<SymbolId, FunctionEffectSummary>? effectSummaries = null)
    {
        var functions = MirFunctionOptimizationIndex.Build(module);
        var recursiveCalls = RecursiveCallAnalysis.Analyze(functions);
        var functionSummaries = FunctionOptimizationSummaryAnalyzer.Analyze(
            functions,
            effectSummaries,
            recursiveCalls);
        return new FunctionOptimizationProofIndex(functionSummaries, recursiveCalls);
    }
}

internal static class MirFunctionOptimizationIndex
{
    public static Dictionary<string, MirFunc> Build(MirModule module) =>
        module.Functions
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
}

public static class FunctionOptimizationSummaryAnalyzer
{
    public static FunctionOptimizationSummaryIndex Analyze(
        MirModule module,
        IReadOnlyDictionary<SymbolId, FunctionEffectSummary>? effectSummaries = null)
    {
        var functions = MirFunctionOptimizationIndex.Build(module);
        return Analyze(
            functions,
            effectSummaries,
            RecursiveCallAnalysis.Analyze(functions));
    }

    internal static FunctionOptimizationSummaryIndex Analyze(
        IReadOnlyDictionary<string, MirFunc> functions,
        IReadOnlyDictionary<SymbolId, FunctionEffectSummary>? effectSummaries,
        RecursiveCallAnalysisResult recursiveCalls)
    {
        var local = new Dictionary<string, FunctionOptimizationSummary>(StringComparer.Ordinal);
        var calls = new Dictionary<string, List<CallEdge>>(StringComparer.Ordinal);
        var hasOwnSummary = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, function) in functions)
        {
            FunctionEffectSummary? effectSummary = null;
            var hasEffectSummary = function.SymbolId.IsValid &&
                                   effectSummaries != null &&
                                   effectSummaries.TryGetValue(function.SymbolId, out effectSummary);
            if (hasEffectSummary)
            {
                hasOwnSummary.Add(key);
            }

            var summary = function.IsExternal
                ? FunctionOptimizationSummary.Unknown with
                {
                    Effects = hasEffectSummary
                        ? effectSummary!.DeclaredUpperBound.Union(effectSummary.InferredEffects)
                        : EffectRow.Pure
                }
                : hasEffectSummary
                    ? FunctionOptimizationSummary.FromTrustedEffects(effectSummary!)
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
                            summary = hasEffectSummary
                                ? summary.JoinEffects(FunctionOptimizationSummary.Unknown)
                                : summary.Join(FunctionOptimizationSummary.Unknown);
                            break;
                    }
                }
            }

            local[key] = summary;
            calls[key] = functionCalls;
        }

        foreach (var component in recursiveCalls.Components)
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
                var trustOwnSummary = hasOwnSummary.Contains(key);
                foreach (var call in calls[key])
                {
                    if (call.IsPartial)
                    {
                        continue;
                    }

                    var calleeSummary = result.GetValueOrDefault(
                        call.CalleeKey,
                        FunctionOptimizationSummary.Unknown);
                    summary = trustOwnSummary
                        ? summary.JoinEffects(calleeSummary)
                        : summary.Join(calleeSummary);
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

internal interface IFunctionOptimizationProofConsumer
{
    FunctionOptimizationProofIndex FunctionProofs { set; }
}
