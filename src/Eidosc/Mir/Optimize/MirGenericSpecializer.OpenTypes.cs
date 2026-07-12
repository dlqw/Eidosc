using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private bool ContainsOpenTypeVariable(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (_containsOpenTypeVariableByTypeId.TryGetValue(typeId.Value, out var cached))
        {
            return cached;
        }

        var containsOpenTypeVariable = MirGenericAnalysis.ContainsOpenTypeVariable(
            typeId,
            _dynamicTypes.DescriptorByIdDict,
            _dynamicTypes.KeyByIdDict,
            IsOpenUninternedType);
        _containsOpenTypeVariableByTypeId[typeId.Value] = containsOpenTypeVariable;
        return containsOpenTypeVariable;
    }

    private bool IsGenericSignature(MirFunc function)
    {
        return function.IsGenericSignature(
            _dynamicTypes.DescriptorByIdDict,
            _dynamicTypes.KeyByIdDict,
            MirGenericLocalScope.ParametersOnly,
            IsOpenUninternedType);
    }

    private bool IsGenericTemplateCandidate(MirFunc function)
    {
        return function.GenericParameterCount > 0 ||
               IsGenericSignature(function) ||
               IsGeneratedTemplateClosureFunction(function) && ContainsOpenMirTypes(function);
    }

    private bool IsOpenUninternedType(TypeId typeId)
    {
        return !BaseTypes.IsBuiltIn(typeId) &&
               (IsMirGenericTypeParameter(typeId) ||
                TryGetTypeDescriptor(typeId, out var descriptor) && descriptor is TypeDescriptor.TypeVar);
    }

    private bool IsMirGenericTypeParameter(TypeId typeId)
    {
        return typeId.IsValid && _genericTypeParameterTypeIds.Contains(typeId.Value);
    }

    private bool ContainsOpenLocalTypes(MirFunc function)
    {
        if (ContainsOpenTypeVariable(function.ReturnType))
        {
            return true;
        }

        foreach (var local in function.Locals)
        {
            if (local.IsParameter && ContainsOpenTypeVariable(local.TypeId))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsOpenConstructorBinding(MirFunc function)
    {
        if (ContainsOpenConstructorBinding(function.ReturnType))
        {
            return true;
        }

        foreach (var local in function.Locals)
        {
            if (local.IsParameter && ContainsOpenConstructorBinding(local.TypeId))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsOpenConstructorBinding(TypeId typeId)
    {
        return MirGenericAnalysis.ContainsOpenConstructorVariable(
            typeId,
            _dynamicTypes.DescriptorByIdDict,
            _dynamicTypes.KeyByIdDict);
    }

    private bool ContainsOpenMirTypes(MirFunc function)
    {
        if (ContainsOpenTypeVariable(function.ReturnType))
        {
            return true;
        }

        foreach (var local in function.Locals)
        {
            if (ContainsOpenTypeVariable(local.TypeId))
            {
                return true;
            }
        }

        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (ContainsOpenTypeVariable(instruction))
                {
                    return true;
                }
            }

            if (block.Terminator != null &&
                ContainsOpenTypeVariable(block.Terminator))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsOpenTypeVariable(MirInstruction instruction)
    {
        return instruction switch
        {
            MirAssign assign =>
                ContainsOpenTypeVariable(assign.Target) ||
                ContainsOpenTypeVariable(assign.Source),
            MirCall call =>
                (call.Target != null && ContainsOpenTypeVariable(call.Target)) ||
                ContainsOpenTypeVariable(call.Function) ||
                OperandsContainOpenTypeVariable(call.Arguments),
            MirBinOp binOp =>
                ContainsOpenTypeVariable(binOp.Target) ||
                ContainsOpenTypeVariable(binOp.Left) ||
                ContainsOpenTypeVariable(binOp.Right),
            MirUnaryOp unaryOp =>
                ContainsOpenTypeVariable(unaryOp.Target) ||
                ContainsOpenTypeVariable(unaryOp.Operand),
            MirLoad load =>
                ContainsOpenTypeVariable(load.Target) ||
                ContainsOpenTypeVariable(load.Source),
            MirStore store =>
                ContainsOpenTypeVariable(store.Target) ||
                ContainsOpenTypeVariable(store.Value),
            MirDrop drop => ContainsOpenTypeVariable(drop.Value),
            MirCopy copy =>
                ContainsOpenTypeVariable(copy.Target) ||
                ContainsOpenTypeVariable(copy.Source),
            MirMove move =>
                ContainsOpenTypeVariable(move.Target) ||
                ContainsOpenTypeVariable(move.Source),
            MirAlloc alloc =>
                ContainsOpenTypeVariable(alloc.Target) ||
                ContainsOpenTypeVariable(alloc.TypeId),
            _ => false
        };
    }

    private bool ContainsOpenTypeVariable(MirTerminator terminator)
    {
        return terminator switch
        {
            MirReturn ret => ret.Value != null && ContainsOpenTypeVariable(ret.Value),
            MirSwitch sw =>
                ContainsOpenTypeVariable(sw.Discriminant) ||
                SwitchBranchesContainOpenTypeVariable(sw.Branches),
            _ => false
        };
    }

    private bool OperandsContainOpenTypeVariable(IReadOnlyList<MirOperand> operands)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            if (ContainsOpenTypeVariable(operands[i]))
            {
                return true;
            }
        }

        return false;
    }

    private bool SwitchBranchesContainOpenTypeVariable(IReadOnlyList<MirSwitchBranch> branches)
    {
        for (var i = 0; i < branches.Count; i++)
        {
            if (ContainsOpenTypeVariable(branches[i].Value))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsOpenTypeVariable(MirOperand operand)
    {
        return operand switch
        {
            MirPlace place => ContainsOpenTypeVariable(place),
            MirFunctionRef => false,
            _ => ContainsOpenOperandTypeVariable(operand.TypeId)
        };
    }

    private bool ContainsOpenTypeVariable(MirPlace place)
    {
        if (ContainsOpenOperandTypeVariable(place.TypeId))
        {
            return true;
        }

        return (place.Base != null && ContainsOpenTypeVariable(place.Base)) ||
               (place.Index != null && ContainsOpenTypeVariable(place.Index));
    }

    private bool ContainsOpenOperandTypeVariable(TypeId typeId)
    {
        return typeId.IsValid && ContainsOpenTypeVariable(typeId);
    }
}
