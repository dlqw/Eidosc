using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// MIR 优化器框架 - 管理和执行优化 Pass
/// </summary>
public sealed partial class MirOptimizer
{
    private readonly List<IMirOptimizationPass> _passes = [];
    private readonly Func<string, IDisposable>? _measureSubphase;
    private readonly IReadOnlyDictionary<SymbolId, FunctionEffectSummary>? _effectSummaries;

    public MirOptimizer(
        Func<string, IDisposable>? measureSubphase = null,
        IReadOnlyDictionary<SymbolId, FunctionEffectSummary>? effectSummaries = null)
    {
        _measureSubphase = measureSubphase;
        _effectSummaries = effectSummaries;
    }

    /// <summary>
    /// 已注册的优化 Pass 名称（按执行顺序）
    /// </summary>
    public List<string> PassNames => _passes.Select(pass => pass.Name).ToList();

    /// <summary>
    /// 注册优化 Pass
    /// </summary>
    public void RegisterPass(IMirOptimizationPass pass)
    {
        _passes.Add(pass);
    }

    /// <summary>
    /// 执行所有优化 Pass
    /// </summary>
    public MirModule Optimize(MirModule module)
    {
        return OptimizeWithResult(module).Module;
    }

    public MirOptimizationResult OptimizeWithResult(MirModule module)
    {
        var current = module;
        var changeKind = MirOptimizationChangeKind.None;
        var passStats = new List<MirOptimizationPassStats>(_passes.Count);

        for (var passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            var pass = _passes[passIndex];
            var before = current;
            if (pass is IFunctionOptimizationSummaryConsumer summaryConsumer)
            {
                summaryConsumer.FunctionSummaries =
                    FunctionOptimizationSummaryAnalyzer.Analyze(current, _effectSummaries);
            }
            using (MeasureOptimizerSubphase($"pass.{passIndex}.{pass.Name}"))
            {
                current = pass.Run(current);
            }

            if (ReferenceEquals(before, current))
            {
                passStats.Add(new MirOptimizationPassStats(
                    passIndex,
                    pass.Name,
                    false,
                    MirOptimizationChangeKind.None,
                    before.Functions.Count,
                    current.Functions.Count));
                continue;
            }

            var passChangeKind = ClassifyChange(before, current);
            changeKind = MirOptimizationChangeKindExtensions.Max(
                changeKind,
                passChangeKind);
            passStats.Add(new MirOptimizationPassStats(
                passIndex,
                pass.Name,
                true,
                passChangeKind,
                before.Functions.Count,
                current.Functions.Count));
        }

        return new MirOptimizationResult(current, changeKind, passStats);
    }

    /// <summary>
    /// 创建默认优化器
    /// </summary>
    public static MirOptimizer CreateDefault(
        Func<string, IDisposable>? measureSubphase = null,
        IReadOnlyDictionary<SymbolId, FunctionEffectSummary>? effectSummaries = null)
    {
        var optimizer = new MirOptimizer(measureSubphase, effectSummaries);

        // Round 1: local simplification and conservative effect-aware motion
        optimizer.RegisterPass(new ConstantFolding());
        optimizer.RegisterPass(new CommonSubexpressionElimination());
        optimizer.RegisterPass(new LoopInvariantCodeMotion());
        optimizer.RegisterPass(new DeadCodeElimination());

        // Round 2: Tail call optimization.
        // Inlining and DropInsertion are intentionally kept out of the default
        // path until their native-code edge cases are fully proven.
        optimizer.RegisterPass(new TailCallOptimization(convertSelfRecursionToLoop: true));
        optimizer.RegisterPass(new DeadCodeElimination());

        return optimizer;
    }

    private IDisposable MeasureOptimizerSubphase(string name)
    {
        return _measureSubphase?.Invoke(name) ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        private NullDisposable()
        {
        }

        public void Dispose()
        {
        }
    }
}

public readonly record struct MirOptimizationResult(
    MirModule Module,
    MirOptimizationChangeKind ChangeKind,
    IReadOnlyList<MirOptimizationPassStats> PassStats)
{
    public bool Changed => ChangeKind != MirOptimizationChangeKind.None;
}

public readonly record struct MirOptimizationPassStats(
    int PassIndex,
    string PassName,
    bool Changed,
    MirOptimizationChangeKind ChangeKind,
    int InputFunctionCount,
    int OutputFunctionCount);

public enum MirOptimizationChangeKind
{
    None = 0,
    LocalOnly = 1,
    CallGraphChanged = 2,
    SignatureChanged = 3
}

internal static class MirOptimizationChangeKindExtensions
{
    public static bool AffectsSpecialization(this MirOptimizationChangeKind changeKind)
    {
        return changeKind >= MirOptimizationChangeKind.CallGraphChanged;
    }

    public static MirOptimizationChangeKind Max(
        MirOptimizationChangeKind left,
        MirOptimizationChangeKind right)
    {
        return left >= right ? left : right;
    }
}

/// <summary>
/// MIR 优化 Pass 接口
/// </summary>
public interface IMirOptimizationPass
{
    /// <summary>
    /// Pass 名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 执行优化
    /// </summary>
    MirModule Run(MirModule module);
}

public sealed partial class MirOptimizer
{
    private static MirOptimizationChangeKind ClassifyChange(MirModule before, MirModule after)
    {
        if (HasSignatureChange(before, after))
        {
            return MirOptimizationChangeKind.SignatureChanged;
        }

        return IntroducesFunctionReference(before, after)
            ? MirOptimizationChangeKind.CallGraphChanged
            : MirOptimizationChangeKind.LocalOnly;
    }

    private static bool HasSignatureChange(MirModule before, MirModule after)
    {
        if (before.Functions.Count != after.Functions.Count)
        {
            return true;
        }

        for (var i = 0; i < before.Functions.Count; i++)
        {
            var left = before.Functions[i];
            var right = after.Functions[i];
            if (!SameFunctionIdentity(left, right) ||
                left.ReturnType != right.ReturnType ||
                left.GenericParameterCount != right.GenericParameterCount ||
                left.IsRuntimeWordAbi != right.IsRuntimeWordAbi ||
                left.IsExternal != right.IsExternal ||
                left.GenericTypeParameterIds.Count != right.GenericTypeParameterIds.Count)
            {
                return true;
            }

            for (var genericIndex = 0; genericIndex < left.GenericTypeParameterIds.Count; genericIndex++)
            {
                if (left.GenericTypeParameterIds[genericIndex] != right.GenericTypeParameterIds[genericIndex])
                {
                    return true;
                }
            }

            if (!SameParameterTypes(left, right))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameParameterTypes(MirFunc left, MirFunc right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (true)
        {
            var hasLeftParameter = TryReadNextParameter(left.Locals, ref leftIndex, out var leftParameter);
            var hasRightParameter = TryReadNextParameter(right.Locals, ref rightIndex, out var rightParameter);

            if (!hasLeftParameter && !hasRightParameter)
            {
                return true;
            }

            if (hasLeftParameter != hasRightParameter ||
                leftParameter.TypeId != rightParameter.TypeId)
            {
                return false;
            }
        }
    }

    private static bool TryReadNextParameter(
        IReadOnlyList<MirLocal> locals,
        ref int index,
        out MirLocal parameter)
    {
        while (index < locals.Count)
        {
            var local = locals[index++];
            if (local.IsParameter)
            {
                parameter = local;
                return true;
            }
        }

        parameter = null!;
        return false;
    }

    private static bool SameFunctionIdentity(MirFunc left, MirFunc right)
    {
        if (left.FunctionId.IsValid || right.FunctionId.IsValid)
        {
            return left.FunctionId == right.FunctionId;
        }

        if (left.SymbolId.IsValid || right.SymbolId.IsValid)
        {
            return left.SymbolId == right.SymbolId;
        }

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private static bool IntroducesFunctionReference(MirModule before, MirModule after)
    {
        var beforeReferences = new HashSet<ulong>();
        CollectFunctionReferenceFingerprints(before, beforeReferences);

        foreach (var function in after.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (ContainsNewInstructionFunctionRef(instruction, beforeReferences))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void CollectFunctionReferenceFingerprints(MirModule module, HashSet<ulong> fingerprints)
    {
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    CollectInstructionFunctionRefs(instruction, fingerprints);
                }
            }
        }
    }

    private static void CollectInstructionFunctionRefs(MirInstruction instruction, HashSet<ulong> fingerprints)
    {
        switch (instruction)
        {
            case MirAssign assign:
                CollectOperandFunctionRefs(assign.Source, fingerprints);
                break;
            case MirCaseInject injection:
                CollectOperandFunctionRefs(injection.Target, fingerprints);
                CollectOperandFunctionRefs(injection.Operand, fingerprints);
                break;
            case MirCall call:
                CollectOperandFunctionRefs(call.Function, fingerprints);
                foreach (var argument in call.Arguments)
                {
                    CollectOperandFunctionRefs(argument, fingerprints);
                }

                break;
            case MirBinOp binOp:
                CollectOperandFunctionRefs(binOp.Left, fingerprints);
                CollectOperandFunctionRefs(binOp.Right, fingerprints);
                break;
            case MirUnaryOp unaryOp:
                CollectOperandFunctionRefs(unaryOp.Operand, fingerprints);
                break;
            case MirLoad load:
                CollectOperandFunctionRefs(load.Source, fingerprints);
                break;
            case MirStore store:
                CollectPlaceFunctionRefs(store.Target, fingerprints);
                CollectOperandFunctionRefs(store.Value, fingerprints);
                break;
            case MirDrop drop:
                CollectOperandFunctionRefs(drop.Value, fingerprints);
                break;
            case MirCopy copy:
                CollectPlaceFunctionRefs(copy.Source, fingerprints);
                break;
            case MirMove move:
                CollectPlaceFunctionRefs(move.Source, fingerprints);
                break;
        }
    }

    private static bool ContainsNewInstructionFunctionRef(MirInstruction instruction, HashSet<ulong> existingFingerprints)
    {
        return instruction switch
        {
            MirAssign assign => ContainsNewOperandFunctionRef(assign.Source, existingFingerprints),
            MirCaseInject injection =>
                ContainsNewOperandFunctionRef(injection.Target, existingFingerprints) ||
                ContainsNewOperandFunctionRef(injection.Operand, existingFingerprints),
            MirCall call => ContainsNewOperandFunctionRef(call.Function, existingFingerprints) ||
                            ContainsNewOperandFunctionRef(call.Arguments, existingFingerprints),
            MirBinOp binOp => ContainsNewOperandFunctionRef(binOp.Left, existingFingerprints) ||
                              ContainsNewOperandFunctionRef(binOp.Right, existingFingerprints),
            MirUnaryOp unaryOp => ContainsNewOperandFunctionRef(unaryOp.Operand, existingFingerprints),
            MirLoad load => ContainsNewOperandFunctionRef(load.Source, existingFingerprints),
            MirStore store => ContainsNewPlaceFunctionRef(store.Target, existingFingerprints) ||
                              ContainsNewOperandFunctionRef(store.Value, existingFingerprints),
            MirDrop drop => ContainsNewOperandFunctionRef(drop.Value, existingFingerprints),
            MirCopy copy => ContainsNewPlaceFunctionRef(copy.Source, existingFingerprints),
            MirMove move => ContainsNewPlaceFunctionRef(move.Source, existingFingerprints),
            _ => false
        };
    }

    private static void CollectOperandsFunctionRefs(IReadOnlyList<MirOperand> operands, HashSet<ulong> fingerprints)
    {
        foreach (var operand in operands)
        {
            CollectOperandFunctionRefs(operand, fingerprints);
        }
    }

    private static void CollectOperandFunctionRefs(MirOperand? operand, HashSet<ulong> fingerprints)
    {
        switch (operand)
        {
            case MirFunctionRef functionRef:
                fingerprints.Add(CreateFunctionRefFingerprint(functionRef));
                break;
            case MirPlace place:
                CollectPlaceFunctionRefs(place, fingerprints);
                break;
        }
    }

    private static void CollectPlaceFunctionRefs(MirPlace? place, HashSet<ulong> fingerprints)
    {
        if (place == null)
        {
            return;
        }

        CollectOperandFunctionRefs(place.Index, fingerprints);
        CollectPlaceFunctionRefs(place.Base, fingerprints);
    }

    private static bool ContainsNewOperandFunctionRef(
        IReadOnlyList<MirOperand> operands,
        HashSet<ulong> existingFingerprints)
    {
        foreach (var operand in operands)
        {
            if (ContainsNewOperandFunctionRef(operand, existingFingerprints))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsNewOperandFunctionRef(MirOperand? operand, HashSet<ulong> existingFingerprints)
    {
        return operand switch
        {
            MirFunctionRef functionRef => !existingFingerprints.Contains(CreateFunctionRefFingerprint(functionRef)),
            MirPlace place => ContainsNewPlaceFunctionRef(place, existingFingerprints),
            _ => false
        };
    }

    private static bool ContainsNewPlaceFunctionRef(MirPlace? place, HashSet<ulong> existingFingerprints)
    {
        return place != null &&
               (ContainsNewOperandFunctionRef(place.Index, existingFingerprints) ||
                ContainsNewPlaceFunctionRef(place.Base, existingFingerprints));
    }

    private static ulong CreateFunctionRefFingerprint(MirFunctionRef functionRef)
    {
        var hash = 14695981039346656037UL;
        AddHash(ref hash, functionRef.SymbolId.Value);
        AddFunctionId(ref hash, functionRef.FunctionId);
        AddHash(ref hash, functionRef.TypeId.Value);
        AddHash(ref hash, functionRef.TraitOwnerId.Value);
        AddHash(ref hash, (int)functionRef.TraitSelfPosition);
        AddHash(ref hash, functionRef.TraitSelfInResult ? 1 : 0);
        AddHash(ref hash, (int)functionRef.TraitMethodRole);
        AddHash(ref hash, functionRef.TraitSelfParameterIndices.Count);
        foreach (var index in functionRef.TraitSelfParameterIndices)
        {
            AddHash(ref hash, index);
        }

        AddString(ref hash, functionRef.Name);
        return hash;
    }

    private static void AddFunctionId(ref ulong hash, FunctionId? functionId)
    {
        if (functionId == null)
        {
            AddHash(ref hash, 0);
            return;
        }

        AddHash(ref hash, functionId.SymbolId.Value);
        AddHash(ref hash, (int)functionId.Kind);
        AddString(ref hash, functionId.Module);
        AddString(ref hash, functionId.ModuleIdentityKey);
        AddString(ref hash, functionId.Name);
        AddString(ref hash, functionId.QualifiedName);
    }

    private static void AddString(ref ulong hash, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            AddHash(ref hash, 0);
            return;
        }

        for (var i = 0; i < value.Length; i++)
        {
            AddHash(ref hash, value[i]);
        }
    }

    private static void AddHash(ref ulong hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 1099511628211UL;
        }
    }
}
