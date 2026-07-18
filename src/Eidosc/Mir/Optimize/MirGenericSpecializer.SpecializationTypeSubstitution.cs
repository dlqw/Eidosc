using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private TypeId SubstituteTypeId(TypeId typeId, SpecializationBindings bindings)
    {
        if (!typeId.IsValid || bindings.Count == 0)
        {
            return typeId;
        }

        return CreateSpecializationTypeSubstitutionService().SubstituteTypeId(typeId, bindings);
    }

    private TypeId SubstituteTypeId(TypeId typeId, SpecializationBindings bindings, HashSet<int> resolvingTypeIds)
    {
        if (!typeId.IsValid || bindings.Count == 0)
        {
            return typeId;
        }

        return CreateSpecializationTypeSubstitutionService().SubstituteTypeId(typeId, bindings, resolvingTypeIds);
    }

    private TypeId GetOrCreateDynamicTypeId(string typeKey)
    {
        return TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor)
            ? GetOrCreateDynamicTypeId(descriptor)
            : GetOrCreateLegacyDynamicTypeId(typeKey);
    }

    private TypeId GetOrCreateDynamicTypeId(TypeDescriptor descriptor)
    {
        if (_dynamicTypes.IdByDescriptorDict.TryGetValue(descriptor, out var existingTypeId))
        {
            EnsureDynamicTypeMetadata(existingTypeId, descriptor);
            return existingTypeId;
        }

        var typeKey = descriptor.ToString();
        if (_dynamicTypes.IdByKeyDict.TryGetValue(typeKey, out existingTypeId))
        {
            _dynamicTypes.IdByDescriptorDict[descriptor] = existingTypeId;
            EnsureDynamicTypeMetadata(existingTypeId, descriptor);
            return existingTypeId;
        }

        var newTypeId = new TypeId(_dynamicTypes.NextId++);
        _dynamicTypes.IdByDescriptorDict[descriptor] = newTypeId;
        _dynamicTypes.IdByKeyDict[typeKey] = newTypeId;
        _dynamicTypes.KeyByIdDict[newTypeId.Value] = typeKey;
        _dynamicTypes.DescriptorByIdDict[newTypeId.Value] = descriptor;
        return newTypeId;
    }

    private bool TryGetInternedDynamicTypeId(TypeDescriptor descriptor, out TypeId typeId)
    {
        if (_dynamicTypes.IdByDescriptorDict.TryGetValue(descriptor, out typeId))
        {
            EnsureDynamicTypeMetadata(typeId, descriptor);
            return true;
        }

        var typeKey = descriptor.ToString();
        if (_dynamicTypes.IdByKeyDict.TryGetValue(typeKey, out typeId))
        {
            _dynamicTypes.IdByDescriptorDict[descriptor] = typeId;
            EnsureDynamicTypeMetadata(typeId, descriptor);
            return true;
        }

        typeId = TypeId.None;
        return false;
    }

    private TypeId GetOrCreateLegacyDynamicTypeId(string typeKey)
    {
        if (_dynamicTypes.IdByKeyDict.TryGetValue(typeKey, out var existingTypeId))
        {
            return existingTypeId;
        }

        var newTypeId = new TypeId(_dynamicTypes.NextId++);
        _dynamicTypes.IdByKeyDict[typeKey] = newTypeId;
        _dynamicTypes.KeyByIdDict[newTypeId.Value] = typeKey;
        if (TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor))
        {
            _dynamicTypes.DescriptorByIdDict[newTypeId.Value] = descriptor;
            _dynamicTypes.IdByDescriptorDict[descriptor] = newTypeId;
        }
        return newTypeId;
    }

    private void EnsureDynamicTypeMetadata(TypeId typeId, TypeDescriptor descriptor)
    {
        var typeKey = descriptor.ToString();
        _dynamicTypes.DescriptorByIdDict[typeId.Value] = descriptor;
        _dynamicTypes.KeyByIdDict.TryAdd(typeId.Value, typeKey);
        _dynamicTypes.IdByKeyDict.TryAdd(typeKey, typeId);
    }

    private bool TryGetTypeDescriptor(TypeId typeId, out TypeDescriptor descriptor)
    {
        if (_dynamicTypes.DescriptorByIdDict.TryGetValue(typeId.Value, out descriptor!))
        {
            return true;
        }

        if (_dynamicTypes.KeyByIdDict.TryGetValue(typeId.Value, out var typeKey) &&
            TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor!))
        {
            _dynamicTypes.DescriptorByIdDict[typeId.Value] = descriptor;
            _dynamicTypes.IdByDescriptorDict[descriptor] = typeId;
            return true;
        }

        descriptor = null!;
        return false;
    }

    private TypeId[] SubstituteTypeIds(
        IReadOnlyList<TypeId> typeIds,
        SpecializationBindings bindings,
        HashSet<int> resolvingTypeIds,
        out bool changed)
    {
        if (bindings.Count == 0)
        {
            changed = false;
            return typeIds as TypeId[] ?? [.. typeIds];
        }

        return CreateSpecializationTypeSubstitutionService().SubstituteTypeIds(typeIds, bindings, resolvingTypeIds, out changed);
    }

    private SpecializationTypeSubstitutionService CreateSpecializationTypeSubstitutionService()
    {
        return new SpecializationTypeSubstitutionService(this);
    }

    private List<MirBasicBlock> CloneBlocksWithTypeSubstitution(
        IReadOnlyList<MirBasicBlock> blocks,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        var clonedBlocks = new List<MirBasicBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            clonedBlocks.Add(CloneBlockWithTypeSubstitution(block, bindings, substitutionService, resolvingTypeIds));
        }

        return clonedBlocks;
    }

    private MirBasicBlock CloneBlockWithTypeSubstitution(MirBasicBlock block, SpecializationBindings bindings)
    {
        var substitutionService = CreateSpecializationTypeSubstitutionService();
        var resolvingTypeIds = new HashSet<int>();
        return CloneBlockWithTypeSubstitution(block, bindings, substitutionService, resolvingTypeIds);
    }

    private MirBasicBlock CloneBlockWithTypeSubstitution(
        MirBasicBlock block,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        var clonedInstructions = new List<MirInstruction>(block.Instructions.Count);
        foreach (var instruction in block.Instructions)
        {
            clonedInstructions.Add(CloneInstructionWithTypeSubstitution(instruction, bindings, substitutionService, resolvingTypeIds));
        }

        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = clonedInstructions,
            Terminator = block.Terminator is null
                ? null
                : CloneTerminatorWithTypeSubstitution(block.Terminator, bindings, substitutionService, resolvingTypeIds),
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    private MirInstruction CloneInstructionWithTypeSubstitution(MirInstruction instruction, SpecializationBindings bindings)
    {
        var substitutionService = CreateSpecializationTypeSubstitutionService();
        var resolvingTypeIds = new HashSet<int>();
        return CloneInstructionWithTypeSubstitution(instruction, bindings, substitutionService, resolvingTypeIds);
    }

    private MirInstruction CloneInstructionWithTypeSubstitution(
        MirInstruction instruction,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        return instruction switch
        {
            MirAssign assign => assign with
            {
                Target = ClonePlaceWithTypeSubstitution(assign.Target, bindings, substitutionService, resolvingTypeIds),
                Source = CloneOperandWithTypeSubstitution(assign.Source, bindings, substitutionService, resolvingTypeIds)
            },
            MirCaseInject injection => injection with
            {
                Target = CloneOperandWithTypeSubstitution(injection.Target, bindings, substitutionService, resolvingTypeIds),
                Operand = CloneOperandWithTypeSubstitution(injection.Operand, bindings, substitutionService, resolvingTypeIds),
                SourceTypeId = substitutionService.SubstituteTypeId(injection.SourceTypeId, bindings, resolvingTypeIds),
                TargetTypeId = substitutionService.SubstituteTypeId(injection.TargetTypeId, bindings, resolvingTypeIds)
            },
            MirCall call => call with
            {
                Target = call.Target is null ? null : ClonePlaceWithTypeSubstitution(call.Target, bindings, substitutionService, resolvingTypeIds),
                Function = CloneOperandWithTypeSubstitution(call.Function, bindings, substitutionService, resolvingTypeIds),
                Arguments = CloneOperandsWithTypeSubstitution(call.Arguments, bindings, substitutionService, resolvingTypeIds)
            },
            MirBinOp binOp => binOp with
            {
                Target = CloneOperandWithTypeSubstitution(binOp.Target, bindings, substitutionService, resolvingTypeIds),
                Left = CloneOperandWithTypeSubstitution(binOp.Left, bindings, substitutionService, resolvingTypeIds),
                Right = CloneOperandWithTypeSubstitution(binOp.Right, bindings, substitutionService, resolvingTypeIds)
            },
            MirUnaryOp unaryOp => unaryOp with
            {
                Target = CloneOperandWithTypeSubstitution(unaryOp.Target, bindings, substitutionService, resolvingTypeIds),
                Operand = CloneOperandWithTypeSubstitution(unaryOp.Operand, bindings, substitutionService, resolvingTypeIds)
            },
            MirLoad load => load with
            {
                Target = ClonePlaceWithTypeSubstitution(load.Target, bindings, substitutionService, resolvingTypeIds),
                Source = CloneOperandWithTypeSubstitution(load.Source, bindings, substitutionService, resolvingTypeIds)
            },
            MirStore store => store with
            {
                Target = ClonePlaceWithTypeSubstitution(store.Target, bindings, substitutionService, resolvingTypeIds),
                Value = CloneOperandWithTypeSubstitution(store.Value, bindings, substitutionService, resolvingTypeIds)
            },
            MirDrop drop => drop with { Value = CloneOperandWithTypeSubstitution(drop.Value, bindings, substitutionService, resolvingTypeIds) },
            MirCopy copy => copy with
            {
                Target = ClonePlaceWithTypeSubstitution(copy.Target, bindings, substitutionService, resolvingTypeIds),
                Source = ClonePlaceWithTypeSubstitution(copy.Source, bindings, substitutionService, resolvingTypeIds)
            },
            MirMove move => move with
            {
                Target = ClonePlaceWithTypeSubstitution(move.Target, bindings, substitutionService, resolvingTypeIds),
                Source = ClonePlaceWithTypeSubstitution(move.Source, bindings, substitutionService, resolvingTypeIds)
            },
            MirAlloc alloc => alloc with
            {
                Target = ClonePlaceWithTypeSubstitution(alloc.Target, bindings, substitutionService, resolvingTypeIds),
                TypeId = substitutionService.SubstituteTypeId(alloc.TypeId, bindings, resolvingTypeIds)
            },
            _ => CloneInstruction(instruction)
        };
    }

    private List<MirOperand> CloneOperandsWithTypeSubstitution(
        IReadOnlyList<MirOperand> operands,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        var clonedOperands = new List<MirOperand>(operands.Count);
        foreach (var operand in operands)
        {
            clonedOperands.Add(CloneOperandWithTypeSubstitution(operand, bindings, substitutionService, resolvingTypeIds));
        }

        return clonedOperands;
    }

    private MirTerminator CloneTerminatorWithTypeSubstitution(MirTerminator terminator, SpecializationBindings bindings)
    {
        var substitutionService = CreateSpecializationTypeSubstitutionService();
        var resolvingTypeIds = new HashSet<int>();
        return CloneTerminatorWithTypeSubstitution(terminator, bindings, substitutionService, resolvingTypeIds);
    }

    private MirTerminator CloneTerminatorWithTypeSubstitution(
        MirTerminator terminator,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        return terminator switch
        {
            MirReturn ret => ret with
            {
                Value = ret.Value is null ? null : CloneOperandWithTypeSubstitution(ret.Value, bindings, substitutionService, resolvingTypeIds)
            },
            MirSwitch sw => sw with
            {
                Discriminant = CloneOperandWithTypeSubstitution(sw.Discriminant, bindings, substitutionService, resolvingTypeIds),
                Branches = CloneSwitchBranchesWithTypeSubstitution(sw.Branches, bindings, substitutionService, resolvingTypeIds)
            },
            _ => CloneTerminator(terminator)
        };
    }

    private List<MirSwitchBranch> CloneSwitchBranchesWithTypeSubstitution(
        IReadOnlyList<MirSwitchBranch> branches,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        var clonedBranches = new List<MirSwitchBranch>(branches.Count);
        foreach (var branch in branches)
        {
            clonedBranches.Add(branch with
            {
                Value = (MirConstant)CloneOperandWithTypeSubstitution(branch.Value, bindings, substitutionService, resolvingTypeIds)
            });
        }

        return clonedBranches;
    }

    private MirOperand CloneOperandWithTypeSubstitution(MirOperand operand, SpecializationBindings bindings)
    {
        var substitutionService = CreateSpecializationTypeSubstitutionService();
        var resolvingTypeIds = new HashSet<int>();
        return CloneOperandWithTypeSubstitution(operand, bindings, substitutionService, resolvingTypeIds);
    }

    private MirOperand CloneOperandWithTypeSubstitution(
        MirOperand operand,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        return operand switch
        {
            MirPlace place => ClonePlaceWithTypeSubstitution(place, bindings, substitutionService, resolvingTypeIds),
            MirFunctionRef functionRef => functionRef with
            {
                TypeId = substitutionService.SubstituteTypeId(functionRef.TypeId, bindings, resolvingTypeIds),
                SignatureTypeId = substitutionService.SubstituteTypeId(functionRef.SignatureTypeId, bindings, resolvingTypeIds),
                TypeArgumentIds = substitutionService.SubstituteTypeIds(functionRef.TypeArgumentIds, bindings, resolvingTypeIds, out _),
                ValueArguments = SubstituteGenericValueArguments(
                    functionRef.ValueArguments,
                    bindings,
                    substitutionService,
                    resolvingTypeIds)
            },
            MirConstant constant => constant with { TypeId = substitutionService.SubstituteTypeId(constant.TypeId, bindings, resolvingTypeIds) },
            MirTemp temp => temp with { TypeId = substitutionService.SubstituteTypeId(temp.TypeId, bindings, resolvingTypeIds) },
            _ => CloneOperand(operand)
        };
    }

    private static IReadOnlyList<GenericValueArgumentDescriptor> SubstituteGenericValueArguments(
        IReadOnlyList<GenericValueArgumentDescriptor> valueArguments,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        if (valueArguments.Count == 0)
        {
            return valueArguments;
        }

        var changed = false;
        var substituted = new GenericValueArgumentDescriptor[valueArguments.Count];
        for (var index = 0; index < valueArguments.Count; index++)
        {
            var argument = valueArguments[index];
            var typeId = substitutionService.SubstituteTypeId(argument.TypeId, bindings, resolvingTypeIds);
            substituted[index] = argument with { TypeId = typeId };
            changed |= typeId != argument.TypeId;
        }

        return changed ? substituted : valueArguments;
    }

    private MirPlace ClonePlaceWithTypeSubstitution(MirPlace place, SpecializationBindings bindings)
    {
        var substitutionService = CreateSpecializationTypeSubstitutionService();
        var resolvingTypeIds = new HashSet<int>();
        return ClonePlaceWithTypeSubstitution(place, bindings, substitutionService, resolvingTypeIds);
    }

    private MirPlace ClonePlaceWithTypeSubstitution(
        MirPlace place,
        SpecializationBindings bindings,
        SpecializationTypeSubstitutionService substitutionService,
        HashSet<int> resolvingTypeIds)
    {
        return place with
        {
            TypeId = substitutionService.SubstituteTypeId(place.TypeId, bindings, resolvingTypeIds),
            Base = place.Base is null ? null : ClonePlaceWithTypeSubstitution(place.Base, bindings, substitutionService, resolvingTypeIds),
            Index = place.Index is null ? null : CloneOperandWithTypeSubstitution(place.Index, bindings, substitutionService, resolvingTypeIds)
        };
    }
}
