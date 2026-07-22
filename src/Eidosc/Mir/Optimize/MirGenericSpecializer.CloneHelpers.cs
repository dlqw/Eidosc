namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private static MirFunc CloneFunction(MirFunc function)
    {
        return new MirFunc
        {
            Name = function.Name,
            SourceName = function.SourceName,
            Locals = function.Locals.Select(CloneLocal).ToList(),
            BasicBlocks = function.BasicBlocks.Select(CloneBlock).ToList(),
            EntryBlockId = function.EntryBlockId,
            ReturnType = function.ReturnType,
            GenericParameterCount = function.GenericParameterCount,
            GenericParameters = function.GenericParameters.ToList(),
            GenericTypeParameterIds = function.GenericTypeParameterIds.ToList(),
            Span = function.Span,
            SymbolId = function.SymbolId,
            FunctionId = function.FunctionId,
            TraitInvokeHelper = function.TraitInvokeHelper,
            TraitInvokeHelperTraitId = function.TraitInvokeHelperTraitId,
            IsRuntimeWordAbi = function.IsRuntimeWordAbi,
            IsEntry = function.IsEntry,
            IsExternal = function.IsExternal,
            ExternalSymbolName = function.ExternalSymbolName,
            ExternalLibrary = function.ExternalLibrary,
            IntrinsicName = function.IntrinsicName,
            BuiltinIntrinsicRole = function.BuiltinIntrinsicRole
        };
    }

    private static MirLocal CloneLocal(MirLocal local)
    {
        return new MirLocal
        {
            Id = local.Id,
            Name = local.Name,
            TypeId = local.TypeId,
            IsMutable = local.IsMutable,
            IsParameter = local.IsParameter,
            BindingMode = local.BindingMode,
            Span = local.Span
        };
    }

    private static MirBasicBlock CloneBlock(MirBasicBlock block)
    {
        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = block.Instructions.Select(CloneInstruction).ToList(),
            Terminator = block.Terminator is null ? null : CloneTerminator(block.Terminator),
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    private static MirInstruction CloneInstruction(MirInstruction instruction)
    {
        return instruction switch
        {
            MirAssign assign => assign with
            {
                Target = assign.Target,
                Source = assign.Source
            },
            MirCaseInject injection => injection with { },
            MirCall call => call with
            {
                Arguments = call.Arguments.ToList()
            },
            MirBinOp binOp => binOp with { },
            MirUnaryOp unaryOp => unaryOp with { },
            MirLoad load => load with { },
            MirStore store => store with { },
            MirDrop drop => drop with { },
            MirCopy copy => copy with { },
            MirMove move => move with { },
            MirAlloc alloc => alloc with { },
            _ => instruction
        };
    }

    private static MirTerminator CloneTerminator(MirTerminator terminator)
    {
        return terminator switch
        {
            MirReturn ret => ret with
            { },
            MirGoto jump => jump with { },
            MirSwitch sw => sw with
            {
                Branches = sw.Branches.Select(branch => new MirSwitchBranch
                {
                    Value = branch.Value,
                    Target = branch.Target,
                    BoundVariable = branch.BoundVariable
                }).ToList(),
                DefaultTarget = sw.DefaultTarget
            },
            MirUnreachable unreachable => unreachable with { },
            _ => terminator
        };
    }

    private static MirOperand CloneOperand(MirOperand operand) => operand;
}
