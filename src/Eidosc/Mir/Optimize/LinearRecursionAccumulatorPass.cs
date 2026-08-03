using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Rewrites the linear-recursion shape F(n) = F(n-1) + F(n-2) with base case
/// F(k) = k (k &lt; 2) into a single-tail-call accumulator loop, roughly halving
/// the recursive call count (mirrors the transform Rust tooling applies before
/// LLVM; see docs/research/compiler-call-layer-2026-08-02).
///
/// v1 matches the exact shape only; any deviation returns the function
/// unchanged. Equivalence: with F(n) = F(n-1) + F(n-2) and F(0) = 0, F(1) = 1,
/// F(n) = Σ F(n-1-2k) + F(n mod 2), which is exactly the generated loop
/// (acc += F(n-1); n -= 2; return acc + n). The transform is restricted to the
/// canonical Int type, whose current LLVM lowering uses plain wrapping add/sub;
/// the regrouping therefore remains associative even after 64-bit overflow.
/// </summary>
public sealed class LinearRecursionAccumulatorPass : IMirOptimizationPass, IFunctionOptimizationSummaryConsumer
{
    private FunctionOptimizationSummaryIndex _functionSummaries = FunctionOptimizationSummaryIndex.Empty;

    public string Name => "LinearRecursionAccumulator";

    FunctionOptimizationSummaryIndex IFunctionOptimizationSummaryConsumer.FunctionSummaries
    {
        set => _functionSummaries = value;
    }

    public MirModule Run(MirModule module)
    {
        List<MirFunc>? optimizedFunctions = null;

        for (var i = 0; i < module.Functions.Count; i++)
        {
            var func = module.Functions[i];
            var optimized = OptimizeFunction(func);
            if (optimizedFunctions != null)
            {
                optimizedFunctions.Add(optimized);
                continue;
            }

            if (!ReferenceEquals(optimized, func))
            {
                optimizedFunctions = new List<MirFunc>(module.Functions.Count);
                for (var previous = 0; previous < i; previous++)
                {
                    optimizedFunctions.Add(module.Functions[previous]);
                }

                optimizedFunctions.Add(optimized);
            }
        }

        if (optimizedFunctions == null)
        {
            return module;
        }

        return CloneModuleWithFunctions(module, optimizedFunctions);
    }

    private MirFunc OptimizeFunction(MirFunc func)
    {
        if (func.IsExternal ||
            func.BasicBlocks.Count == 0 ||
            func.GenericParameterCount > 0 ||
            func.Locals.Count(static local => local.IsParameter) != 1 ||
            func.ReturnType.Value != BaseTypes.IntId ||
            !_functionSummaries.TryGet(func, out var summary) ||
            !summary.CanReassociatePureCalls)
        {
            return func;
        }

        if (!TryMatchFibShape(func, out var shape))
        {
            return func;
        }

        return RewriteToAccumulatorLoop(func, shape!);
    }

    private sealed record FibShape(
        MirLocal Parameter,
        BlockId BaseBlockId,
        BlockId RecursionBlockId,
        MirCall FirstCall);

    private readonly record struct ValueKey(bool IsTemp, int Value);

    private readonly record struct Definition(int Index, MirInstruction Instruction);

    private static bool TryMatchFibShape(MirFunc func, out FibShape? shape)
    {
        shape = null;

        var blocks = func.BasicBlocks;
        if (blocks.Count != 3)
        {
            return false;
        }

        var parameter = func.Locals.Single(static local => local.IsParameter);
        if (parameter.TypeId != func.ReturnType)
        {
            return false;
        }

        var paramPlace = CreateLocalPlace(parameter.Id, parameter.TypeId, func.Span);
        var paramKey = OperandTargetKey(paramPlace)!.Value;

        var entries = blocks.Where(static block => block.IsEntry).ToArray();
        if (entries.Length != 1 || entries[0].Id != func.EntryBlockId)
        {
            return false;
        }

        var entry = entries[0];
        var baseBlock = blocks.FirstOrDefault(block => !block.IsEntry && IsBaseReturn(block, paramPlace));
        var recBlock = blocks.FirstOrDefault(block => !block.IsEntry && block != baseBlock);
        if (entry == null || baseBlock == null || recBlock == null)
        {
            return false;
        }

        // Entry: switch true -> base, default -> rec, guarded by n < 2
        // (possibly wrapped as Eq(Lt(n, 2), true), the usual if lowering).
        if (entry.Terminator is not MirSwitch entrySwitch ||
            entrySwitch.Branches.Count != 1 ||
            !IsBoolConstant(entrySwitch.Branches[0].Value, true) ||
            !entrySwitch.Branches[0].Target.Equals(baseBlock.Id) ||
            !entrySwitch.DefaultTarget.HasValue ||
            !entrySwitch.DefaultTarget.Value.Equals(recBlock.Id) ||
            !TryMatchEntryGuard(entry, entrySwitch, paramKey))
        {
            return false;
        }

        if (!TryBuildDefinitions(recBlock, out var definitions))
        {
            return false;
        }

        var subtractions = recBlock.Instructions
            .Select((instruction, index) => (instruction, index))
            .Where(static pair => pair.instruction is MirBinOp { Operator: BinaryOp.Sub })
            .Select(static pair => ((MirBinOp)pair.instruction, pair.index))
            .ToArray();
        var calls = recBlock.Instructions
            .Select((instruction, index) => (instruction, index))
            .Where(static pair => pair.instruction is MirCall)
            .Select(static pair => ((MirCall)pair.instruction, pair.index))
            .ToArray();
        var additions = recBlock.Instructions
            .Select((instruction, index) => (instruction, index))
            .Where(static pair => pair.instruction is MirBinOp { Operator: BinaryOp.Add })
            .Select(static pair => ((MirBinOp)pair.instruction, pair.index))
            .ToArray();
        if (subtractions.Length != 2 || calls.Length != 2 || additions.Length != 1 ||
            recBlock.Instructions.Any(static instruction => instruction is not MirCopy and
                                                               not MirCall and
                                                               not MirBinOp { Operator: BinaryOp.Sub or BinaryOp.Add }))
        {
            return false;
        }

        var consumed = new HashSet<int>();
        var offsetsByTarget = new Dictionary<ValueKey, long>();
        foreach (var (subtraction, index) in subtractions)
        {
            if (OperandTargetKey(subtraction.Target) is not { } target ||
                subtraction.Target.TypeId != parameter.TypeId ||
                subtraction.Left.TypeId != parameter.TypeId ||
                subtraction.Right.TypeId != parameter.TypeId ||
                !TryGetIntConstant(subtraction.Right, out var offset) ||
                !TryResolveCopyRoot(
                    subtraction.Left,
                    definitions,
                    index,
                    consumed,
                    out var leftRoot) ||
                leftRoot != paramKey ||
                !offsetsByTarget.TryAdd(target, offset))
            {
                return false;
            }

            consumed.Add(index);
        }

        var offsets = offsetsByTarget.Values.OrderBy(static value => value).ToArray();
        if (offsets[0] != 1 || offsets[1] != 2)
        {
            return false;
        }

        var callsByTarget = new Dictionary<ValueKey, (MirCall Call, long Offset)>();
        foreach (var (call, index) in calls)
        {
            if (!IsSelfRecursiveCall(func, call) ||
                call.Target == null ||
                call.Target.TypeId != func.ReturnType ||
                call.Arguments.Count != 1 ||
                call.Arguments[0].TypeId != parameter.TypeId ||
                call.BorrowedArgumentIndices.Count != 0 ||
                call.RecordUpdate != null ||
                call.IsTailCall ||
                OperandTargetKey(call.Target) is not { } callTarget ||
                !TryResolveCopyRoot(call.Arguments[0], definitions, index, consumed, out var argumentRoot) ||
                !definitions.TryGetValue(argumentRoot, out var argumentDefinition) ||
                argumentDefinition.Index >= index ||
                !offsetsByTarget.TryGetValue(argumentRoot, out var offset) ||
                !callsByTarget.TryAdd(callTarget, (call, offset)))
            {
                return false;
            }

            consumed.Add(index);
        }

        if (callsByTarget.Values.Select(static value => value.Offset).ToHashSet().Count != 2)
        {
            return false;
        }

        var (sum, sumIndex) = additions[0];
        if (OperandTargetKey(sum.Target) is not { } sumTarget ||
            sum.Target.TypeId != func.ReturnType ||
            sum.Left.TypeId != func.ReturnType ||
            sum.Right.TypeId != func.ReturnType ||
            !TryResolveCopyRoot(sum.Left, definitions, sumIndex, consumed, out var leftCallRoot) ||
            !TryResolveCopyRoot(sum.Right, definitions, sumIndex, consumed, out var rightCallRoot) ||
            leftCallRoot == rightCallRoot ||
            !callsByTarget.ContainsKey(leftCallRoot) ||
            !callsByTarget.ContainsKey(rightCallRoot) ||
            !definitions.TryGetValue(leftCallRoot, out var leftCallDefinition) ||
            !definitions.TryGetValue(rightCallRoot, out var rightCallDefinition) ||
            leftCallDefinition.Index >= sumIndex ||
            rightCallDefinition.Index >= sumIndex)
        {
            return false;
        }

        consumed.Add(sumIndex);
        if (recBlock.Terminator is not MirReturn { Value: { } returnValue } ||
            returnValue.TypeId != func.ReturnType ||
            !TryResolveCopyRoot(
                returnValue,
                definitions,
                recBlock.Instructions.Count,
                consumed,
                out var returnRoot) ||
            returnRoot != sumTarget ||
            !definitions.TryGetValue(returnRoot, out var returnDefinition) ||
            returnDefinition.Index >= recBlock.Instructions.Count ||
            consumed.Count != recBlock.Instructions.Count)
        {
            return false;
        }

        var firstCall = callsByTarget.Values.Single(static pair => pair.Offset == 1).Call;
        shape = new FibShape(
            parameter,
            baseBlock.Id,
            recBlock.Id,
            firstCall);
        return true;
    }

    private static bool IsBaseReturn(MirBasicBlock block, MirPlace paramPlace)
    {
        return block.Instructions.Count == 0 &&
               block.Terminator is MirReturn ret &&
               ret.Value?.TypeId == paramPlace.TypeId &&
               IsSamePlace(ret.Value as MirPlace, paramPlace);
    }

    private static MirFunc RewriteToAccumulatorLoop(MirFunc func, FibShape shape)
    {
        var blocks = func.BasicBlocks.ToList();
        var locals = func.Locals.ToList();
        var nextLocalId = locals.Select(static local => local.Id.Value).DefaultIfEmpty(0).Max() + 1;
        var nextBlockId = blocks.Select(static block => block.Id.Value).DefaultIfEmpty(0).Max() + 1;

        var loopId = new BlockId { Value = nextBlockId++ };
        var doneId = new BlockId { Value = nextBlockId++ };
        var initId = new BlockId { Value = nextBlockId++ };

        var acc = CreateSyntheticLocal(locals, ref nextLocalId, "__fib_acc", func.ReturnType, func.Span, mutable: true);
        var tmp = CreateSyntheticLocal(locals, ref nextLocalId, "__fib_tmp", shape.Parameter.TypeId, func.Span, mutable: true);
        var arg = CreateSyntheticLocal(locals, ref nextLocalId, "__fib_arg", shape.Parameter.TypeId, func.Span, mutable: false);
        var callResult = CreateSyntheticLocal(locals, ref nextLocalId, "__fib_call", func.ReturnType, func.Span, mutable: false);
        var doneSum = CreateSyntheticLocal(locals, ref nextLocalId, "__fib_sum", func.ReturnType, func.Span, mutable: false);
        var loopCond = CreateSyntheticLocal(locals, ref nextLocalId, "__fib_cond", new TypeId(BaseTypes.BoolId), func.Span, mutable: false);

        // Parameter n becomes mutable (updated in the loop).
        for (var i = 0; i < locals.Count; i++)
        {
            if (!locals[i].IsParameter || locals[i].IsMutable)
            {
                continue;
            }

            locals[i] = new MirLocal
            {
                Id = locals[i].Id,
                Name = locals[i].Name,
                TypeId = locals[i].TypeId,
                IsMutable = true,
                IsParameter = locals[i].IsParameter,
                BindingMode = locals[i].BindingMode,
                Span = locals[i].Span
            };
        }

        var paramPlace = CreateLocalPlace(shape.Parameter.Id, shape.Parameter.TypeId, func.Span);
        var one = IntConstant(1, shape.Parameter.TypeId, func.Span);
        var two = IntConstant(2, shape.Parameter.TypeId, func.Span);
        var zero = IntConstant(0, func.ReturnType, func.Span);
        var trueConst = new MirConstant
        {
            Value = new MirConstantValue.BoolValue(true),
            TypeId = new TypeId(BaseTypes.BoolId),
            Span = func.Span
        };

        // Replace the recursion-block edge with init (acc := 0 -> loop).
        var entry = blocks.First(static block => block.IsEntry);
        var entryIndex = blocks.IndexOf(entry);
        var entrySwitch = (MirSwitch)entry.Terminator!;
        blocks[entryIndex] = new MirBasicBlock
        {
            Id = entry.Id,
            Instructions = entry.Instructions,
            Terminator = new MirSwitch
            {
                Discriminant = entrySwitch.Discriminant,
                Branches = [new MirSwitchBranch { Value = trueConst, Target = shape.BaseBlockId }],
                DefaultTarget = initId,
                Span = entry.Span
            },
            Span = entry.Span,
            IsEntry = true
        };

        // init: acc := 0; goto loop
        blocks.Add(new MirBasicBlock
        {
            Id = initId,
            Instructions =
            [
                new MirAssign { Target = acc, Source = zero, Span = func.Span }
            ],
            Terminator = new MirGoto { Target = loopId, Span = func.Span },
            Span = func.Span,
            IsEntry = false
        });

        // loop: tmp := n; arg := tmp - 1; n := tmp - 2;
        //       r := tail call F(arg); acc := acc + r; if n < 2 goto done else loop
        blocks.Add(new MirBasicBlock
        {
            Id = loopId,
            Instructions =
            [
                new MirAssign { Target = tmp, Source = paramPlace, Span = func.Span },
                new MirBinOp
                {
                    Target = arg,
                    Operator = BinaryOp.Sub,
                    Left = tmp,
                    Right = one,
                    Span = func.Span
                },
                new MirBinOp
                {
                    Target = paramPlace,
                    Operator = BinaryOp.Sub,
                    Left = tmp,
                    Right = two,
                    Span = func.Span
                },
                new MirCall
                {
                    Target = callResult,
                    Function = shape.FirstCall.Function,
                    Arguments = [arg],
                    Span = shape.FirstCall.Span
                },
                new MirBinOp
                {
                    Target = acc,
                    Operator = BinaryOp.Add,
                    Left = acc,
                    Right = callResult,
                    Span = func.Span
                },
                new MirBinOp
                {
                    Target = loopCond,
                    Operator = BinaryOp.Lt,
                    Left = paramPlace,
                    Right = two,
                    Span = func.Span
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = loopCond,
                Branches = [new MirSwitchBranch { Value = trueConst, Target = doneId }],
                DefaultTarget = loopId,
                Span = func.Span
            },
            Span = func.Span,
            IsEntry = false
        });

        // done: sum := acc + n; return sum
        blocks.Add(new MirBasicBlock
        {
            Id = doneId,
            Instructions =
            [
                new MirBinOp
                {
                    Target = doneSum,
                    Operator = BinaryOp.Add,
                    Left = acc,
                    Right = paramPlace,
                    Span = func.Span
                }
            ],
            Terminator = new MirReturn { Value = doneSum, Span = func.Span },
            Span = func.Span,
            IsEntry = false
        });

        blocks.RemoveAll(block => block.Id == shape.RecursionBlockId);

        return MirFunctionTransform.CloneWithBody(func, locals, blocks);
    }

    private static bool TryMatchEntryGuard(
        MirBasicBlock entry,
        MirSwitch entrySwitch,
        ValueKey parameterKey)
    {
        if (entrySwitch.Discriminant.TypeId.Value != BaseTypes.BoolId ||
            entry.Instructions.Any(static instruction => instruction is not MirCopy and
                                                              not MirBinOp { Operator: BinaryOp.Lt or BinaryOp.Eq }) ||
            !TryBuildDefinitions(entry, out var definitions))
        {
            return false;
        }

        var consumed = new HashSet<int>();
        if (!TryResolveCopyRoot(
                entrySwitch.Discriminant,
                definitions,
                entry.Instructions.Count,
                consumed,
                out var guardRoot) ||
            !definitions.TryGetValue(guardRoot, out var guardDefinition) ||
            guardDefinition.Index >= entry.Instructions.Count)
        {
            return false;
        }

        Definition lessThanDefinition;
        if (guardDefinition.Instruction is MirBinOp { Operator: BinaryOp.Eq } equality)
        {
            if (equality.Target.TypeId.Value != BaseTypes.BoolId ||
                equality.Left.TypeId.Value != BaseTypes.BoolId ||
                equality.Right.TypeId.Value != BaseTypes.BoolId ||
                !IsBoolConstant(equality.Right, true) ||
                !TryResolveCopyRoot(
                    equality.Left,
                    definitions,
                    guardDefinition.Index,
                    consumed,
                    out var lessThanRoot) ||
                !definitions.TryGetValue(lessThanRoot, out lessThanDefinition) ||
                lessThanDefinition.Index >= guardDefinition.Index)
            {
                return false;
            }

            consumed.Add(guardDefinition.Index);
        }
        else
        {
            lessThanDefinition = guardDefinition;
        }

        if (lessThanDefinition.Instruction is not MirBinOp { Operator: BinaryOp.Lt } lessThan ||
            lessThan.Target.TypeId.Value != BaseTypes.BoolId ||
            lessThan.Left.TypeId.Value != BaseTypes.IntId ||
            lessThan.Right.TypeId.Value != BaseTypes.IntId ||
            !IsIntConstant(lessThan.Right, 2) ||
            !TryResolveCopyRoot(
                lessThan.Left,
                definitions,
                lessThanDefinition.Index,
                consumed,
                out var leftRoot) ||
            leftRoot != parameterKey)
        {
            return false;
        }

        consumed.Add(lessThanDefinition.Index);
        return consumed.Count == entry.Instructions.Count;
    }

    private static bool TryBuildDefinitions(
        MirBasicBlock block,
        out Dictionary<ValueKey, Definition> definitions)
    {
        definitions = [];
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            var target = block.Instructions[index] switch
            {
                MirCopy copy => copy.Target,
                MirCall call => call.Target,
                MirBinOp binOp => binOp.Target,
                _ => null
            };
            if (target == null)
            {
                continue;
            }

            if (OperandTargetKey(target) is not { } key ||
                !definitions.TryAdd(key, new Definition(index, block.Instructions[index])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveCopyRoot(
        MirOperand operand,
        IReadOnlyDictionary<ValueKey, Definition> definitions,
        int useIndex,
        HashSet<int> consumed,
        out ValueKey root)
    {
        if (OperandTargetKey(operand) is not { } current)
        {
            root = default;
            return false;
        }

        var visited = new HashSet<ValueKey>();
        while (definitions.TryGetValue(current, out var definition) &&
               definition.Instruction is MirCopy copy)
        {
            if (!visited.Add(current) ||
                definition.Index >= useIndex ||
                copy.Target.TypeId != copy.Source.TypeId ||
                OperandTargetKey(copy.Source) is not { } source)
            {
                root = default;
                return false;
            }

            consumed.Add(definition.Index);
            current = source;
            useIndex = definition.Index;
        }

        root = current;
        return true;
    }

    private static ValueKey? OperandTargetKey(MirOperand? operand)
    {
        return operand switch
        {
            MirPlace { Kind: PlaceKind.Local } place => new ValueKey(false, place.Local.Value),
            MirTemp temp => new ValueKey(true, temp.Id.Value),
            _ => null
        };
    }

    private static bool IsBoolConstant(MirOperand operand, bool expected)
    {
        return operand is MirConstant constant &&
               constant.Value is MirConstantValue.BoolValue boolValue &&
               boolValue.Value == expected;
    }

    private static bool IsIntConstant(MirOperand operand, long expected)
    {
        return TryGetIntConstant(operand, out var value) && value == expected;
    }

    private static bool TryGetIntConstant(MirOperand operand, out long value)
    {
        if (operand is MirConstant constant && constant.Value is MirConstantValue.IntValue intValue)
        {
            value = intValue.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private static MirConstant IntConstant(long value, TypeId typeId, SourceSpan span)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.IntValue(value),
            TypeId = typeId,
            Span = span
        };
    }

    private static MirPlace CreateSyntheticLocal(
        List<MirLocal> locals,
        ref int nextLocalId,
        string name,
        TypeId typeId,
        SourceSpan span,
        bool mutable)
    {
        var localId = new LocalId { Value = nextLocalId++ };
        locals.Add(new MirLocal
        {
            Id = localId,
            Name = name,
            TypeId = typeId,
            IsMutable = mutable,
            IsParameter = false,
            Span = span
        });

        return CreateLocalPlace(localId, typeId, span);
    }

    private static MirPlace CreateLocalPlace(LocalId localId, TypeId typeId, SourceSpan span)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId,
            TypeId = typeId,
            Span = span
        };
    }

    private static bool IsSelfRecursiveCall(MirFunc func, MirCall call)
    {
        if (call.Function is not MirFunctionRef funcRef ||
            funcRef.SymbolKind != SymbolKind.Function)
        {
            return false;
        }

        if (MirFunctionIdentity.TryGetStableKey(func.FunctionId, out var functionKey) &&
            MirFunctionIdentity.TryGetStableKey(funcRef.FunctionId, out var referenceKey))
        {
            return string.Equals(functionKey, referenceKey, StringComparison.Ordinal);
        }

        if (func.SymbolId.IsValid || funcRef.SymbolId.IsValid)
        {
            return func.SymbolId.IsValid &&
                   funcRef.SymbolId.IsValid &&
                   func.SymbolId.Equals(funcRef.SymbolId);
        }

        return !string.IsNullOrWhiteSpace(func.Name) &&
               string.Equals(funcRef.Name, func.Name, StringComparison.Ordinal);
    }

    private static bool IsSamePlace(MirPlace? left, MirPlace? right)
    {
        if (left == null || right == null || left.Kind != right.Kind)
        {
            return false;
        }

        return left.Kind switch
        {
            PlaceKind.Local => left.Local.Equals(right.Local),
            PlaceKind.Field => string.Equals(left.FieldName, right.FieldName, StringComparison.Ordinal) &&
                               left.Base != null &&
                               right.Base != null &&
                               IsSamePlace(left.Base, right.Base),
            PlaceKind.Index => left.Base != null &&
                               right.Base != null &&
                               IsSamePlace(left.Base, right.Base) &&
                               Equals(left.Index, right.Index),
            PlaceKind.Deref => left.Base != null &&
                               right.Base != null &&
                               IsSamePlace(left.Base, right.Base),
            _ => false
        };
    }

    private static MirModule CloneModuleWithFunctions(MirModule module, List<MirFunc> functions)
    {
        return new MirModule
        {
            Name = module.Name,
            PackageAlias = module.PackageAlias,
            PackageInstanceKey = module.PackageInstanceKey,
            Path = module.Path.ToList(),
            Functions = functions,
            DynamicTypeKeys = new Dictionary<int, string>(module.DynamicTypeKeys),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(module.TypeDescriptors),
            LinkLibraries = module.LinkLibraries.ToList(),
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>(module.CStructAccessors),
            ConstructorLayouts = module.ConstructorLayouts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToList()),
            CopyLikeTypeIds = new HashSet<int>(module.CopyLikeTypeIds),
            TraitImpls = module.TraitImpls.ToList(),
            TraitInfos = module.TraitInfos.ToList(),
            TypeAliases = module.TypeAliases.ToList(),
            TypeConstructors = module.TypeConstructors.ToList(),
            SpecializationFailures = module.SpecializationFailures.ToList(),
            Span = module.Span
        };
    }
}
