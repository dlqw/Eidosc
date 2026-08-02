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
/// (acc += F(n-1); n -= 2; return acc + n).
/// </summary>
public sealed class LinearRecursionAccumulatorPass : IMirOptimizationPass
{
    public string Name => "LinearRecursionAccumulator";

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
            func.Locals.Count(static local => local.IsParameter) != 1)
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
        MirCall FirstCall,
        MirCall SecondCall);

    private static bool TryMatchFibShape(MirFunc func, out FibShape? shape)
    {
        shape = null;

        var blocks = func.BasicBlocks;
        if (blocks.Count != 3)
        {
            return false;
        }

        var parameter = func.Locals.Single(static local => local.IsParameter);
        var paramPlace = CreateLocalPlace(parameter.Id, parameter.TypeId, func.Span);

        var entry = blocks.FirstOrDefault(static block => block.IsEntry);
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
            !IsEntryGuard(entry, entrySwitch, paramPlace))
        {
            return false;
        }

        // Recursion block: two self calls with args n-1 / n-2, summed and returned.
        var subsByTarget = new Dictionary<(bool IsTemp, int Value), long>();
        foreach (var instruction in recBlock.Instructions)
        {
            if (instruction is not MirBinOp binOp ||
                binOp.Operator != BinaryOp.Sub ||
                !IsSamePlace(ResolveThroughCopies(binOp.Left as MirPlace, recBlock), paramPlace) ||
                !TryGetIntConstant(binOp.Right, out var subValue) ||
                OperandTargetKey(binOp.Target) is not { } subKey)
            {
                continue;
            }

            subsByTarget[subKey] = subValue;
        }

        var calls = new List<(MirCall Call, long Offset)>();
        foreach (var instruction in recBlock.Instructions)
        {
            if (instruction is not MirCall call ||
                !IsSelfRecursiveCall(func, call) ||
                call.Arguments.Count != 1 ||
                OperandTargetKey(call.Arguments[0]) is not { } argKey ||
                !subsByTarget.TryGetValue(argKey, out var offset))
            {
                // Call arguments often go through a copy (e.g. %8 = copy %7);
                // resolve through copies before looking up the subtraction.
                if (instruction is MirCall copyCall &&
                    IsSelfRecursiveCall(func, copyCall) &&
                    copyCall.Arguments.Count == 1 &&
                    ResolveThroughCopies(copyCall.Arguments[0] as MirPlace, recBlock) is { } resolvedArg &&
                    OperandTargetKey(resolvedArg) is { } resolvedKey &&
                    subsByTarget.TryGetValue(resolvedKey, out var resolvedOffset))
                {
                    calls.Add((copyCall, resolvedOffset));
                }

                continue;
            }

            calls.Add((call, offset));
        }

        if (calls.Count != 2 ||
            calls.Select(static pair => pair.Offset).ToHashSet().Count != 2)
        {
            return false;
        }

        var offsets = calls.Select(static pair => pair.Offset).OrderBy(static value => value).ToArray();
        if (offsets[0] != 1 || offsets[1] != 2)
        {
            return false;
        }

        var callTargets = calls
            .Select(static pair => pair.Call.Target)
            .Select(OperandTargetKey)
            .ToHashSet();
        if (callTargets.Count != 2 || callTargets.Any(static key => key == null))
        {
            return false;
        }

        // Copies inserted by later passes (e.g. %14 = copy %9) alias their
        // sources; resolve them so the sum operands match the call targets.
        var copyAliases = new Dictionary<(bool IsTemp, int Value), (bool IsTemp, int Value)>();
        foreach (var instruction in recBlock.Instructions)
        {
            if (instruction is MirCopy copy &&
                OperandTargetKey(copy.Target) is { } aliasTarget &&
                OperandTargetKey(copy.Source) is { } aliasSource)
            {
                copyAliases[aliasTarget] = aliasSource;
            }
        }

        static (bool IsTemp, int Value)? ResolveAlias(
            (bool IsTemp, int Value)? key,
            Dictionary<(bool IsTemp, int Value), (bool IsTemp, int Value)> aliases)
        {
            var current = key;
            for (var depth = 0; depth < 4 && current is { } value && aliases.TryGetValue(value, out var next); depth++)
            {
                current = next;
            }

            return current;
        }

        var sum = recBlock.Instructions.OfType<MirBinOp>().LastOrDefault(static binOp => binOp.Operator == BinaryOp.Add);
        if (sum == null ||
            OperandTargetKey(sum.Target) is not { } sumKey ||
            !callTargets.Contains(ResolveAlias(OperandTargetKey(sum.Left), copyAliases)) ||
            !callTargets.Contains(ResolveAlias(OperandTargetKey(sum.Right), copyAliases)) ||
            recBlock.Terminator is not MirReturn ret ||
            OperandTargetKey(ret.Value) != sumKey)
        {
            return false;
        }

        var ordered = calls.OrderBy(static pair => pair.Offset).Select(static pair => pair.Call).ToArray();
        shape = new FibShape(
            parameter,
            baseBlock.Id,
            ordered[0],
            ordered[1]);
        return true;
    }

    private static bool IsBaseReturn(MirBasicBlock block, MirPlace paramPlace)
    {
        return block.Instructions.Count == 0 &&
               block.Terminator is MirReturn ret &&
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

        // Drop the old recursion block.
        blocks.RemoveAll(block => !block.IsEntry &&
                                 !block.Id.Equals(shape.BaseBlockId) &&
                                 block.Id.Value != initId.Value &&
                                 block.Id.Value != loopId.Value &&
                                 block.Id.Value != doneId.Value);

        return new MirFunc
        {
            Name = func.Name,
            SourceName = func.SourceName,
            Locals = locals,
            BasicBlocks = blocks,
            EntryBlockId = func.EntryBlockId,
            ReturnType = func.ReturnType,
            GenericParameterCount = func.GenericParameterCount,
            GenericParameters = func.GenericParameters.ToList(),
            GenericTypeParameterIds = func.GenericTypeParameterIds.ToList(),
            IsRuntimeWordAbi = func.IsRuntimeWordAbi,
            IsEntry = func.IsEntry,
            Span = func.Span,
            SymbolId = func.SymbolId,
            FunctionId = func.FunctionId,
            TraitInvokeHelper = func.TraitInvokeHelper,
            TraitInvokeHelperTraitId = func.TraitInvokeHelperTraitId,
            IsExternal = func.IsExternal,
            ExternalSymbolName = func.ExternalSymbolName,
            ExternalLibrary = func.ExternalLibrary,
            IntrinsicName = func.IntrinsicName,
            BuiltinIntrinsicRole = func.BuiltinIntrinsicRole
        };
    }

    private static bool IsEntryGuard(MirBasicBlock entry, MirSwitch entrySwitch, MirPlace paramPlace)
    {
        // The switch discriminant is usually Eq(Lt(n, 2), true); unwind at most
        // one Eq(.., true) wrapper and require the Lt(n, 2) guard so the
        // transform never changes the recursion boundary semantics.
        if (entrySwitch.Discriminant is not MirPlace current)
        {
            return false;
        }

        for (var depth = 0; depth < 3; depth++)
        {
            // A plain copy between the guard binop and the switch
            // (e.g. %4 = copy %3) does not change the value; unwind through it.
            var copyDef = entry.Instructions
                .OfType<MirCopy>()
                .LastOrDefault(copy => copy.Target is MirPlace target && IsSamePlace(target, current));
            if (copyDef != null && copyDef.Source is MirPlace copySource)
            {
                current = copySource;
                continue;
            }

            var defining = entry.Instructions
                .OfType<MirBinOp>()
                .LastOrDefault(binOp => binOp.Target is MirPlace target && IsSamePlace(target, current));
            if (defining == null)
            {
                return false;
            }

            if (defining.Operator == BinaryOp.Lt &&
                ResolveThroughCopies(defining.Left as MirPlace, entry) is { } left &&
                IsSamePlace(left, paramPlace) &&
                IsIntConstant(defining.Right, 2))
            {
                return true;
            }

            if (defining.Operator == BinaryOp.Eq &&
                IsBoolConstant(defining.Right, true) &&
                defining.Left is MirPlace next)
            {
                current = next;
                continue;
            }

            return false;
        }

        return false;
    }


    private static (bool IsTemp, int Value)? OperandTargetKey(MirOperand? operand)
    {
        return operand switch
        {
            MirPlace { Kind: PlaceKind.Local } place => (false, place.Local.Value),
            MirTemp temp => (true, temp.Id.Value),
            _ => null
        };
    }

    private static MirPlace? ResolveThroughCopies(MirPlace? place, MirBasicBlock block)
    {
        var current = place;
        for (var depth = 0; depth < 4 && current is { Kind: PlaceKind.Local } local; depth++)
        {
            var copyDef = block.Instructions
                .OfType<MirCopy>()
                .LastOrDefault(copy => copy.Target is MirPlace target && IsSamePlace(target, current));
            if (copyDef == null || copyDef.Source is not MirPlace next)
            {
                break;
            }

            current = next;
        }

        return current;
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

        if (func.SymbolId.IsValid && funcRef.SymbolId.IsValid)
        {
            return func.SymbolId.Equals(funcRef.SymbolId);
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
