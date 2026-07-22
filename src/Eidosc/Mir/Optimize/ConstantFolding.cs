using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// 常量折叠优化 - 在编译时计算常量表达式 + 常量传播
/// </summary>
public sealed class ConstantFolding : IMirOptimizationPass
{
    public string Name => "ConstantFolding";

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

        return new MirModule
        {
            Name = module.Name,
            PackageAlias = module.PackageAlias,
            PackageInstanceKey = module.PackageInstanceKey,
            Path = module.Path.ToList(),
            Functions = optimizedFunctions,
            DynamicTypeKeys = new Dictionary<int, string>(module.DynamicTypeKeys),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(module.TypeDescriptors),
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>(module.CStructAccessors),
            ConstructorLayouts = module.ConstructorLayouts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToList()),
            TraitImpls = module.TraitImpls.ToList(),
            TraitInfos = module.TraitInfos.ToList(),
            TypeAliases = module.TypeAliases.ToList(),
            TypeConstructors = module.TypeConstructors.ToList(),
            LinkLibraries = module.LinkLibraries.ToList(),
            SpecializationFailures = module.SpecializationFailures.ToList(),
            Span = module.Span
        };
    }

    private MirFunc OptimizeFunction(MirFunc func)
    {
        List<MirBasicBlock>? optimizedBlocks = null;

        for (var i = 0; i < func.BasicBlocks.Count; i++)
        {
            var block = func.BasicBlocks[i];
            var optimized = OptimizeBlock(block);
            if (optimizedBlocks != null)
            {
                optimizedBlocks.Add(optimized);
                continue;
            }

            if (!ReferenceEquals(optimized, block))
            {
                optimizedBlocks = new List<MirBasicBlock>(func.BasicBlocks.Count);
                for (var previous = 0; previous < i; previous++)
                {
                    optimizedBlocks.Add(func.BasicBlocks[previous]);
                }

                optimizedBlocks.Add(optimized);
            }
        }

        if (optimizedBlocks == null)
        {
            return func;
        }

        return new MirFunc
        {
            Name = func.Name,
            SourceName = func.SourceName,
            Locals = func.Locals,
            BasicBlocks = optimizedBlocks,
            EntryBlockId = func.EntryBlockId,
            ReturnType = func.ReturnType,
            GenericParameterCount = func.GenericParameterCount,
            GenericParameters = func.GenericParameters.ToList(),
            GenericTypeParameterIds = func.GenericTypeParameterIds.ToList(),
            IsRuntimeWordAbi = func.IsRuntimeWordAbi,
            IsEntry = func.IsEntry,
            IsExternal = func.IsExternal,
            ExternalSymbolName = func.ExternalSymbolName,
            ExternalLibrary = func.ExternalLibrary,
            IntrinsicName = func.IntrinsicName,
            BuiltinIntrinsicRole = func.BuiltinIntrinsicRole,
            Span = func.Span,
            SymbolId = func.SymbolId,
            FunctionId = func.FunctionId,
            TraitInvokeHelper = func.TraitInvokeHelper,
            TraitInvokeHelperTraitId = func.TraitInvokeHelperTraitId
        };
    }

    private MirBasicBlock OptimizeBlock(MirBasicBlock block)
    {
        var knownConstants = new Dictionary<LocalId, MirConstant>();
        List<MirInstruction>? optimizedInstructions = null;

        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var instr = block.Instructions[i];
            var optimized = OptimizeInstruction(instr, knownConstants);
            if (optimizedInstructions != null)
            {
                optimizedInstructions.Add(optimized);
                continue;
            }

            if (!ReferenceEquals(optimized, instr))
            {
                optimizedInstructions = new List<MirInstruction>(block.Instructions.Count);
                for (var previous = 0; previous < i; previous++)
                {
                    optimizedInstructions.Add(block.Instructions[previous]);
                }

                optimizedInstructions.Add(optimized);
            }
        }

        if (optimizedInstructions == null)
        {
            return block;
        }

        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = optimizedInstructions,
            Terminator = block.Terminator,
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    private MirInstruction OptimizeInstruction(MirInstruction instr, Dictionary<LocalId, MirConstant> knownConstants)
    {
        // Handle MirAssign: track constants and propagate
        if (instr is MirAssign assign)
        {
            var source = PropagateOperand(assign.Source, knownConstants);

            // Track constant assignments for propagation
            if (source is MirConstant constVal && assign.Target.Kind == PlaceKind.Local)
            {
                knownConstants[assign.Target.Local] = constVal;
            }
            // Invalidate if a non-constant is assigned to a tracked local
            else if (assign.Target.Kind == PlaceKind.Local)
            {
                knownConstants.Remove(assign.Target.Local);
            }

            if (!ReferenceEquals(source, assign.Source))
            {
                return assign with { Source = source };
            }
            return assign;
        }

        if (instr is MirCaseInject injection)
        {
            var operand = PropagateOperand(injection.Operand, knownConstants);
            if (injection.Target is MirPlace { Kind: PlaceKind.Local } target)
            {
                knownConstants.Remove(target.Local);
            }

            return ReferenceEquals(operand, injection.Operand)
                ? injection
                : injection with { Operand = operand };
        }

        // Handle MirBinOp: propagate + fold
        if (instr is MirBinOp binOp)
        {
            var left = PropagateOperand(binOp.Left, knownConstants);
            var right = PropagateOperand(binOp.Right, knownConstants);

            if (!ReferenceEquals(left, binOp.Left) || !ReferenceEquals(right, binOp.Right))
            {
                binOp = binOp with { Left = left, Right = right };
            }

            return FoldBinOp(binOp, knownConstants);
        }

        // Handle MirUnaryOp: propagate + fold
        if (instr is MirUnaryOp unaryOp)
        {
            var operand = PropagateOperand(unaryOp.Operand, knownConstants);

            if (!ReferenceEquals(operand, unaryOp.Operand))
            {
                unaryOp = unaryOp with { Operand = operand };
            }

            return FoldUnaryOp(unaryOp, knownConstants);
        }

        // For any other instruction that writes to a local, invalidate it
        if (GetDefinedLocal(instr) is { } definedLocal)
        {
            knownConstants.Remove(definedLocal);
        }

        return instr;
    }

    private static LocalId? GetDefinedLocal(MirInstruction instr)
    {
        return instr switch
        {
            MirAssign { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local } place } => place.Local,
            MirCall { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirLoad { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirAlloc { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirCopy { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirMove { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirStore { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirBinOp { Target: MirPlace { Kind: PlaceKind.Local } place } => place.Local,
            MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local } place } => place.Local,
            _ => null
        };
    }

    /// <summary>
    /// Replace a local reference with its known constant value (if tracked).
    /// </summary>
    private MirOperand PropagateOperand(MirOperand operand, Dictionary<LocalId, MirConstant> knownConstants)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local } place &&
            knownConstants.TryGetValue(place.Local, out var constVal))
        {
            return constVal;
        }
        return operand;
    }

    private MirInstruction FoldBinOp(MirBinOp binOp, Dictionary<LocalId, MirConstant> knownConstants)
    {
        if (binOp.Left is MirConstant leftConst && binOp.Right is MirConstant rightConst)
        {
            var result = TryFoldConstants(binOp.Operator, leftConst, rightConst);
            if (result != null && binOp.Target is MirPlace targetPlace)
            {
                // Track the folded result for further propagation
                if (targetPlace.Kind == PlaceKind.Local)
                {
                    knownConstants[targetPlace.Local] = result;
                }

                return new MirAssign
                {
                    Target = targetPlace,
                    Source = result,
                    Span = binOp.Span
                };
            }
        }

        if (binOp.Target is MirPlace { Kind: PlaceKind.Local } target)
        {
            knownConstants.Remove(target.Local);
        }

        return binOp;
    }

    private MirInstruction FoldUnaryOp(MirUnaryOp unaryOp, Dictionary<LocalId, MirConstant> knownConstants)
    {
        if (unaryOp.Operand is MirConstant operandConst)
        {
            var result = TryFoldUnary(unaryOp.Operator, operandConst);
            if (result != null && unaryOp.Target is MirPlace targetPlace)
            {
                if (targetPlace.Kind == PlaceKind.Local)
                {
                    knownConstants[targetPlace.Local] = result;
                }

                return new MirAssign
                {
                    Target = targetPlace,
                    Source = result,
                    Span = unaryOp.Span
                };
            }
        }

        if (unaryOp.Target is MirPlace { Kind: PlaceKind.Local } target)
        {
            knownConstants.Remove(target.Local);
        }

        return unaryOp;
    }

    // ---- Folding helpers ----

    private MirConstant? TryFoldConstants(BinaryOp op, MirConstant left, MirConstant right)
    {
        return (left.Value, right.Value) switch
        {
            (MirConstantValue.IntValue li, MirConstantValue.IntValue ri) => FoldIntOp(op, li.Value, ri.Value, left.TypeId),
            (MirConstantValue.FloatValue lf, MirConstantValue.FloatValue rf) => FoldFloatOp(op, lf.Value, rf.Value),
            (MirConstantValue.BoolValue lb, MirConstantValue.BoolValue rb) => FoldBoolOp(op, lb.Value, rb.Value),
            (MirConstantValue.StringValue ls, MirConstantValue.StringValue rs) => FoldStringOp(op, ls.Value, rs.Value, left.TypeId),
            _ => null
        };
    }

    private MirConstant? FoldStringOp(BinaryOp op, string left, string right, TypeId typeId)
    {
        return op == BinaryOp.Concat
            ? new MirConstant
            {
                Value = new MirConstantValue.StringValue(left + right),
                TypeId = typeId
            }
            : null;
    }

    private MirConstant? FoldIntOp(BinaryOp op, long left, long right, TypeId typeId)
    {
        // Arithmetic
        try
        {
            long? result = op switch
            {
                BinaryOp.Add => checked(left + right),
                BinaryOp.Sub => checked(left - right),
                BinaryOp.Mul => checked(left * right),
                BinaryOp.Div => right != 0 ? left / right : null,
                BinaryOp.Mod => right != 0 ? left % right : null,
                BinaryOp.And => left & right,
                BinaryOp.Or => left | right,
                _ => null
            };

            if (result.HasValue)
            {
                return new MirConstant
                {
                    Value = new MirConstantValue.IntValue(result.Value),
                    TypeId = typeId
                };
            }
        }
        catch (OverflowException) { }

        // Comparison → Bool
        bool? cmpResult = op switch
        {
            BinaryOp.Eq => left == right,
            BinaryOp.Ne => left != right,
            BinaryOp.Lt => left < right,
            BinaryOp.Le => left <= right,
            BinaryOp.Gt => left > right,
            BinaryOp.Ge => left >= right,
            _ => null
        };

        if (cmpResult.HasValue)
        {
            return new MirConstant
            {
                Value = new MirConstantValue.BoolValue(cmpResult.Value),
                TypeId = new TypeId(BaseTypes.BoolId)
            };
        }

        return null;
    }

    private MirConstant? FoldFloatOp(BinaryOp op, double left, double right)
    {
        // Arithmetic
        double? arithResult = op switch
        {
            BinaryOp.Add => left + right,
            BinaryOp.Sub => left - right,
            BinaryOp.Mul => left * right,
            BinaryOp.Div => right != 0.0 ? left / right : null,
            _ => null
        };

        if (arithResult.HasValue && double.IsFinite(arithResult.Value))
        {
            return new MirConstant
            {
                Value = new MirConstantValue.FloatValue(arithResult.Value),
                TypeId = new TypeId(BaseTypes.FloatId)
            };
        }

        // Comparison → Bool
        bool? cmpResult = op switch
        {
            BinaryOp.Eq => left == right,
            BinaryOp.Ne => left != right,
            BinaryOp.Lt => left < right,
            BinaryOp.Le => left <= right,
            BinaryOp.Gt => left > right,
            BinaryOp.Ge => left >= right,
            _ => null
        };

        if (cmpResult.HasValue)
        {
            return new MirConstant
            {
                Value = new MirConstantValue.BoolValue(cmpResult.Value),
                TypeId = new TypeId(BaseTypes.BoolId)
            };
        }

        return null;
    }

    private MirConstant? FoldBoolOp(BinaryOp op, bool left, bool right)
    {
        bool? result = op switch
        {
            BinaryOp.And => left && right,
            BinaryOp.Or => left || right,
            BinaryOp.Eq => left == right,
            BinaryOp.Ne => left != right,
            _ => null
        };

        if (result.HasValue)
        {
            return new MirConstant
            {
                Value = new MirConstantValue.BoolValue(result.Value),
                TypeId = new TypeId(BaseTypes.BoolId)
            };
        }

        return null;
    }

    private MirConstant? TryFoldUnary(UnaryOp op, MirConstant operand)
    {
        return operand.Value switch
        {
            MirConstantValue.IntValue iv => op switch
            {
                UnaryOp.Neg => new MirConstant
                {
                    Value = new MirConstantValue.IntValue(-iv.Value),
                    TypeId = operand.TypeId
                },
                UnaryOp.Not => new MirConstant
                {
                    Value = new MirConstantValue.IntValue(~iv.Value),
                    TypeId = operand.TypeId
                },
                _ => null
            },
            MirConstantValue.FloatValue fv => op switch
            {
                UnaryOp.Neg => new MirConstant
                {
                    Value = new MirConstantValue.FloatValue(-fv.Value),
                    TypeId = operand.TypeId
                },
                _ => null
            },
            MirConstantValue.BoolValue bv => op switch
            {
                UnaryOp.Not => new MirConstant
                {
                    Value = new MirConstantValue.BoolValue(!bv.Value),
                    TypeId = operand.TypeId
                },
                _ => null
            },
            _ => null
        };
    }
}
